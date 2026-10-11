"""Native RGB24 transport. No resampling, quantization, or error correction.

Each data pixel carries three bytes. Packet CRC and final AES-GCM reject any
corruption; whole frames must be recaptured when their pixels are not exact.
"""
import hashlib
import itertools

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

import colorframe as layout

DATA_WIDTH = layout.GRID_W * layout.CELL
DATA_HEIGHT = layout.GRID_H * layout.CELL
FRAME_BYTES = DATA_WIDTH * DATA_HEIGHT * 3
DEFAULT_SIZE = (1920, 1080)


def validate_size(size):
    width, height = size
    if not (640 <= width <= 3840 and 360 <= height <= 2160):
        raise ValueError('RGB 尺寸須介於 640 × 360 與 3840 × 2160')
    return width, height


def capacity(size=DEFAULT_SIZE):
    width, height = validate_size(size)
    return (width - 128) * (height - 184) * 3


def render(raw, size=DEFAULT_SIZE):
    width, height = validate_size(size)
    frame_bytes = capacity(size)
    if len(raw) > frame_bytes:
        raise ValueError('分片超過 RGB 畫面容量')
    image = Image.new('RGB', size, 'white')
    draw = ImageDraw.Draw(image)
    for index, (x, y) in enumerate(((36, 36), (width - 36, 36), (36, height - 36), (width - 36, height - 36))):
        draw.rectangle((x - 20, y - 20, x + 19, y + 19), fill='black')
        draw.rectangle((x - 12, y - 12, x + 11, y + 11), fill='white')
        draw.rectangle((x - 6, y - 6, x + 5, y + 5), fill=tuple(int(v) for v in layout.PALETTE[index + 2]))
    # Keep a color reference row; native RGB decoding never alters data to match it.
    swatch = min(48, (width - 160) // 16 - 4)
    step = swatch + 4
    start = (width - 16 * step) // 2
    for index, color in enumerate(layout.PALETTE):
        x = start + index * step
        draw.rectangle((x, 32, x + swatch - 1, 79), fill=tuple(int(v) for v in color))
    data = Image.frombytes('RGB', (width - 128, height - 184), raw.ljust(frame_bytes, b'\0'))
    image.paste(data, (layout.DATA_X, layout.DATA_Y))
    return image


def locate(image):
    rgb = np.asarray(image.convert('RGB'))
    labels, _ = ndimage.label(np.max(rgb, axis=2) < 28)
    candidates = []
    for number, region in enumerate(ndimage.find_objects(labels), 1):
        if region is None:
            continue
        ys, xs = region
        # A 40x40 ring must still be 40x40. Never infer away display scaling.
        if xs.stop - xs.start != 40 or ys.stop - ys.start != 40:
            continue
        mask = labels[region] == number
        if not 0.40 < mask.mean() < 0.85 or mask[13:26, 13:26].mean() > 0.15:
            continue
        candidates.append((xs.start + 20, ys.start + 20))
    if len(candidates) > 24:
        raise ValueError('定位候選過多，請縮小擷取範圍')
    for group in itertools.combinations(candidates, 4):
        ordered = sorted(group, key=lambda p: p[1])
        tl, tr = sorted(ordered[:2])
        bl, br = sorted(ordered[2:])
        if tl[1] != tr[1] or bl[1] != br[1] or tl[0] != bl[0] or tr[0] != br[0]:
            continue
        size = (tr[0] - tl[0] + 72, bl[1] - tl[1] + 72)
        try:
            validate_size(size)
        except ValueError:
            continue
        yield tl[0] - 36, tl[1] - 36, size


def read_frames(image):
    rgb = image.convert('RGB')
    for left, top, size in locate(rgb):
        width, height = size
        x, y = left + layout.DATA_X, top + layout.DATA_Y
        data_width, data_height = width - 128, height - 184
        if x < 0 or y < 0 or x + data_width > image.width or y + data_height > image.height:
            continue
        yield rgb.crop((x, y, x + data_width, y + data_height)).tobytes(), size


def read(image):
    for raw, _ in read_frames(image):
        yield raw


def probe_data(size=DEFAULT_SIZE):
    # Public deterministic noise tests the full color range and every data pixel.
    return hashlib.shake_256(b'Pixel Transfer RGB24 RDP probe version 1').digest(capacity(size))
