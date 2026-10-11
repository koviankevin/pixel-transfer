"""Authenticated encrypted files transported in color16 or native RGB frames."""
import hashlib
import math
import os
import struct
import zlib
from pathlib import Path

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.scrypt import Scrypt
import colorframe
import rgbframe

MAX_FILE = 64 * 1024 * 1024
CHUNK = colorframe.FRAME_BYTES - 34
ENVELOPE = struct.Struct('!4s16s12s')  # magic, random KDF salt, random nonce
PACKET = struct.Struct('!4s16sIIH')  # magic, transfer digest, index, total, length
RGB_PACKET = struct.Struct('!4s16sIII')
RGB_SIZED_PACKET = struct.Struct('!4s16sIIIHH')
RGB_CHUNK = rgbframe.FRAME_BYTES - RGB_SIZED_PACKET.size - 4
FORMATS = {b'PTC2': (PACKET, CHUNK), b'PTR3': (RGB_PACKET, rgbframe.FRAME_BYTES - RGB_PACKET.size - 4),
           b'PTR4': (RGB_SIZED_PACKET, RGB_CHUNK)}
MAX_ENVELOPE = MAX_FILE + ENVELOPE.size + 25
MAX_PACKETS = math.ceil(MAX_ENVELOPE / CHUNK)


def key(password, salt):
    if not password:
        raise ValueError('密碼不可空白')
    return Scrypt(salt=salt, length=32, n=2**15, r=8, p=1).derive(password.encode('utf-8'))


def encrypt(data, password):
    if len(data) > MAX_FILE:
        raise ValueError('原型最多支援 64 MiB')
    compressed = zlib.compress(data, level=9)
    use_compression = len(compressed) < len(data)
    plain = struct.pack('!BQ', int(use_compression), len(data))
    plain += compressed if use_compression else data
    salt, nonce = os.urandom(16), os.urandom(12)
    header = ENVELOPE.pack(b'PTE1', salt, nonce)
    return header + AESGCM(key(password, salt)).encrypt(nonce, plain, header)


def decrypt(envelope, password):
    if not ENVELOPE.size + 25 <= len(envelope) <= MAX_ENVELOPE:
        raise ValueError('加密資料長度不合法')
    header = envelope[:ENVELOPE.size]
    magic, salt, nonce = ENVELOPE.unpack(header)
    if magic != b'PTE1':
        raise ValueError('不支援的加密格式')
    try:
        plain = AESGCM(key(password, salt)).decrypt(nonce, envelope[ENVELOPE.size:], header)
    except InvalidTag as exc:
        raise ValueError('密碼錯誤或資料遭到修改') from exc
    flag, size = struct.unpack('!BQ', plain[:9])
    if size > MAX_FILE or flag not in (0, 1):
        raise ValueError('檔案標頭不合法')
    data = plain[9:]
    if flag:
        stream = zlib.decompressobj()
        data = stream.decompress(data, size + 1)
        if not stream.eof or stream.unused_data or stream.unconsumed_tail:
            raise ValueError('壓縮資料不完整或超過宣告長度')
    if len(data) != size:
        raise ValueError('還原長度不符')
    return data


def packets(envelope, mode='color16', frame_size=rgbframe.DEFAULT_SIZE):
    magic = b'PTR4' if mode == 'rgb' else b'PTC2'
    if mode not in ('color16', 'rgb'):
        raise ValueError('不支援的傳輸模式')
    header, chunk = FORMATS[magic]
    if mode == 'rgb':
        chunk = rgbframe.capacity(frame_size) - header.size - 4
    transfer_id = hashlib.sha256(envelope).digest()[:16]
    total = math.ceil(len(envelope) / chunk)
    for index in range(total):
        payload = envelope[index * chunk:(index + 1) * chunk]
        fields = (magic, transfer_id, index, total, len(payload))
        if mode == 'rgb':
            fields += tuple(frame_size)
        body = header.pack(*fields) + payload
        yield body + struct.pack('!I', zlib.crc32(body))


def packet_spec(raw):
    if raw[:4] not in FORMATS:
        raise ValueError('不支援的封包格式')
    header, chunk = FORMATS[raw[:4]]
    if len(raw) < header.size:
        raise ValueError('封包標頭不完整')
    frame_size = rgbframe.DEFAULT_SIZE
    if raw[:4] == b'PTR4':
        frame_size = header.unpack(raw[:header.size])[-2:]
        chunk = rgbframe.capacity(frame_size) - header.size - 4
    return header, chunk, frame_size


def parse_packet(raw):
    header, chunk, _ = packet_spec(raw)
    if len(raw) < header.size + 5 or len(raw) > header.size + chunk + 4:
        raise ValueError('封包長度不合法')
    magic, transfer_id, index, total, size = header.unpack(raw[:header.size])[:5]
    if not 0 <= index < total <= math.ceil(MAX_ENVELOPE / chunk):
        raise ValueError('封包標頭不合法')
    if not 1 <= size <= chunk or len(raw) != header.size + size + 4:
        raise ValueError('封包大小不符')
    if index < total - 1 and size != chunk:
        raise ValueError('中間分片長度不符')
    if zlib.crc32(raw[:-4]) != struct.unpack('!I', raw[-4:])[0]:
        raise ValueError('封包檢查失敗')
    return transfer_id, index, total, raw[header.size:-4]


class Collector:
    """Collect one transfer; ignore duplicates and reject mixed transfers."""
    def __init__(self):
        self.transfer_id = None
        self.total = 0
        self.parts = {}
        self.magic = None
        self.frame_size = None

    def add(self, raw):
        try:
            transfer_id, index, total, payload = parse_packet(raw)
            _, _, frame_size = packet_spec(raw)
        except ValueError:
            return False
        if self.transfer_id is None:
            self.transfer_id, self.total = transfer_id, total
            self.magic = raw[:4]
            self.frame_size = frame_size
        if transfer_id != self.transfer_id or total != self.total or self.magic != raw[:4] or self.frame_size != frame_size:
            raise ValueError('混入其他傳輸的圖像；請只擷取本次傳輸區域')
        if index in self.parts and self.parts[index] != payload:
            raise ValueError('同一分片出現不同內容')
        self.parts[index] = payload
        return True

    @property
    def complete(self):
        return self.total > 0 and len(self.parts) == self.total

    def assemble(self):
        if not self.complete:
            raise ValueError(f'分片不足：{len(self.parts)}/{self.total}')
        envelope = b''.join(self.parts[i] for i in range(self.total))
        if hashlib.sha256(envelope).digest()[:16] != self.transfer_id:
            raise ValueError('完整資料檢查失敗')
        return envelope


def render_packet(raw):
    if raw[:4] in (b'PTR3', b'PTR4'):
        _, _, frame_size = packet_spec(raw)
        return rgbframe.render(raw, frame_size)
    return colorframe.render(raw)


def write_frames(envelope, directory, mode='color16', frame_size=rgbframe.DEFAULT_SIZE):
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=False)
    paths = []
    for index, packet in enumerate(packets(envelope, mode, frame_size)):
        path = directory / f'frame-{index + 1:06d}.png'
        render_packet(packet).save(path)
        paths.append(path)
    return paths


def scan(image, collector, mode='color16'):
    if mode not in ('color16', 'rgb'):
        raise ValueError('不支援的傳輸模式')
    frames = rgbframe.read_frames(image) if mode == 'rgb' else ((data, rgbframe.DEFAULT_SIZE) for data in colorframe.read(image))
    for decoded, detected_size in frames:
        if decoded[:4] not in ((b'PTR3', b'PTR4') if mode == 'rgb' else (b'PTC2',)):
            continue
        try:
            header, _, declared_size = packet_spec(decoded)
        except ValueError:
            continue
        if detected_size != declared_size:
            continue
        size = header.unpack(decoded[:header.size])[4]
        collector.add(decoded[:header.size + size + 4])
