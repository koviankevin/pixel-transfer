"""Native RGB24 transport. No resampling, quantization, or error correction.

Each data pixel carries three bytes. Packet CRC and final AES-GCM reject any
corruption; whole frames must be recaptured when their pixels are not exact.
"""
import hashlib
from PIL import Image, ImageDraw

import colorframe as layout
import markers

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
    for index, (x, y) in enumerate(((36, 36), (width - 36, 36), (36, height - 36))):
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


def read_frames(image):
    rgb = image.convert('RGB')
    for bounds, turns, size in markers.layouts(rgb, native=True):
        left, top, right, bottom = map(round, bounds)
        if left < 0 or top < 0 or right > image.width or bottom > image.height:
            continue
        width, height = size
        aligned = markers.upright(rgb.crop((left, top, right, bottom)), turns)
        x, y = layout.DATA_X, layout.DATA_Y
        data_width, data_height = width - 128, height - 184
        yield aligned.crop((x, y, x + data_width, y + data_height)).tobytes(), size


def read(image):
    for raw, _ in read_frames(image):
        yield raw


def probe_data(size=DEFAULT_SIZE):
    # Public deterministic noise tests the full color range and every data pixel.
    return hashlib.shake_256(b'Pixel Transfer RGB24 RDP probe version 1').digest(capacity(size))
