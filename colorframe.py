"""16-color 1920x1080 screenshot transport with calibration and RS interleaving.

Geometry supports translation, axis-aligned scaling, and quarter-turn rotation.
Camera perspective and arbitrary-angle rotation are outside this prototype.
"""

import numpy as np
from PIL import Image, ImageDraw

import ecc
import markers

WIDTH, HEIGHT = 1920, 1080
CELL = 4
GRID_W, GRID_H = 448, 224
DATA_X, DATA_Y = 64, 104
BLOCKS = (GRID_W * GRID_H // 2) // 255
FRAME_BYTES = BLOCKS * 223
MARKERS = [(36, 36), (1884, 36), (36, 1044)]
PALETTE = np.array([
    (48, 48, 48), (240, 240, 240), (48, 48, 240), (48, 240, 48),
    (240, 48, 48), (48, 240, 240), (240, 48, 240), (240, 240, 48),
    (144, 144, 144), (48, 48, 144), (48, 144, 48), (144, 48, 48),
    (240, 240, 144), (240, 144, 240), (144, 240, 240), (48, 144, 240),
], dtype=np.uint8)


def render(raw):
    if len(raw) > FRAME_BYTES:
        raise ValueError('分片超過畫面容量')
    raw = raw.ljust(FRAME_BYTES, b'\0')
    blocks = np.array([list(ecc.encode(raw[i:i + 223])) for i in range(0, FRAME_BYTES, 223)], dtype=np.uint8)
    # Adjacent screen bytes belong to different RS blocks, spreading local damage.
    wire = blocks.T.flatten()
    symbols = np.zeros(GRID_W * GRID_H, dtype=np.uint8)
    symbols[:len(wire) * 2:2] = wire >> 4
    symbols[1:len(wire) * 2:2] = wire & 15
    pixels = PALETTE[symbols.reshape(GRID_H, GRID_W)]
    data_image = Image.fromarray(pixels).resize((GRID_W * CELL, GRID_H * CELL), Image.Resampling.NEAREST)
    image = reference_canvas()
    image.paste(data_image, (DATA_X, DATA_Y))
    return image


def reference_canvas():
    image = Image.new('RGB', (WIDTH, HEIGHT), 'white')
    draw = ImageDraw.Draw(image)
    for index, (x, y) in enumerate(MARKERS):
        draw.rectangle((x - 20, y - 20, x + 19, y + 19), fill='black')
        draw.rectangle((x - 12, y - 12, x + 11, y + 11), fill='white')
        draw.rectangle((x - 6, y - 6, x + 5, y + 5), fill=tuple(int(v) for v in PALETTE[index + 2]))
    for index, color in enumerate(PALETTE):
        x = 448 + index * 64
        draw.rectangle((x, 32, x + 47, 79), fill=tuple(int(v) for v in color))
    return image


def read_aligned(image):
    rgb = np.asarray(image.convert('RGB'), dtype=np.float32)
    observed = np.array([rgb[44:68, 460 + i * 64:484 + i * 64].mean(axis=(0, 1)) for i in range(16)])
    distances = ((observed[:, None, :] - observed[None, :, :]) ** 2).sum(axis=2)
    np.fill_diagonal(distances, np.inf)
    if distances.min() < 25 ** 2:
        raise ValueError('色彩校正區辨識失敗')
    area = rgb[DATA_Y:DATA_Y + GRID_H * CELL, DATA_X:DATA_X + GRID_W * CELL]
    samples = area.reshape(GRID_H, CELL, GRID_W, CELL, 3)[:, 1:3, :, 1:3, :].mean(axis=(1, 3)).reshape(-1, 3)
    symbols = np.argmin(((samples[:, None, :] - observed[None, :, :]) ** 2).sum(axis=2), axis=1).astype(np.uint8)
    wire = (symbols[::2] << 4) | symbols[1::2]
    blocks = wire[:BLOCKS * 255].reshape(255, BLOCKS).T
    decoded = b''.join(ecc.decode(bytes(block)) for block in blocks)
    return decoded


def read(image):
    for bounds, turns, size in markers.layouts(image, fixed_size=(WIDTH, HEIGHT)):
        try:
            # Bounded EXTENT also handles subpixel marker estimates at screenshot edges.
            physical_size = size[::-1] if turns % 2 else size
            aligned = image.convert('RGB').transform(physical_size, Image.Transform.EXTENT, bounds, Image.Resampling.BILINEAR)
            yield read_aligned(markers.upright(aligned, turns))
        except ValueError:
            continue
