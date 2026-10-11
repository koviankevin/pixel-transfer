"""16-color 1920x1080 screenshot transport with calibration and RS interleaving.

Geometry supports upright screenshots, translation, and axis-aligned scaling.
Camera perspective and screen rotation are deliberately outside version 1.
"""
import itertools

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

import ecc

WIDTH, HEIGHT = 1920, 1080
CELL = 4
GRID_W, GRID_H = 448, 224
DATA_X, DATA_Y = 64, 104
BLOCKS = (GRID_W * GRID_H // 2) // 255
FRAME_BYTES = BLOCKS * 223
MARKERS = [(36, 36), (1884, 36), (36, 1044), (1884, 1044)]
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


def locate(image, native=False):
    # Search at bounded resolution; dark components isolate the black marker rings.
    factor = 1.0 if native else min(1.0, 1200 / max(image.size))
    small = image.resize((round(image.width * factor), round(image.height * factor)), Image.Resampling.BILINEAR)
    rgb = np.asarray(small.convert('RGB'))
    labels, _ = ndimage.label(np.max(rgb, axis=2) < 28)
    candidates = []
    for number, region in enumerate(ndimage.find_objects(labels), 1):
        if region is None:
            continue
        ys, xs = region
        w, h = xs.stop - xs.start, ys.stop - ys.start
        if not 8 <= w <= 100 or not 8 <= h <= 100 or not 0.65 <= w / h <= 1.5:
            continue
        mask = labels[region] == number
        if not 0.40 < mask.mean() < 0.85:
            continue
        # The center is white or colored, not part of the outer black ring.
        if mask[h // 3:2 * h // 3, w // 3:2 * w // 3].mean() > 0.15:
            continue
        candidates.append(((xs.start + xs.stop) / (2 * factor),
                           (ys.start + ys.stop) / (2 * factor), w, h))
    if len(candidates) > 24:
        raise ValueError('定位候選過多，請縮小擷取範圍')
    for group in itertools.combinations(candidates, 4):
        ordered = sorted(group, key=lambda p: p[1])
        tl, tr = sorted(ordered[:2])
        bl, br = sorted(ordered[2:])
        dx, dy = tr[0] - tl[0], bl[1] - tl[1]
        if dx < 300 or dy < 150 or not 1.3 < dx / dy < 2.4:
            continue
        tolerance = max(3, min(tl[2], tl[3]) / factor * 0.15)
        if max(abs(tl[1] - tr[1]), abs(bl[1] - br[1]), abs(tl[0] - bl[0]), abs(tr[0] - br[0])) > tolerance:
            continue
        sx, sy = dx / 1848, dy / 1008
        if any(abs(p[2] / factor / (40 * sx) - 1) > 0.25 or
               abs(p[3] / factor / (40 * sy) - 1) > 0.25 for p in group):
            continue
        left, top = tl[0] - 36 * sx, tl[1] - 36 * sy
        yield (left, top, left + WIDTH * sx, top + HEIGHT * sy)


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
    for bounds in locate(image):
        try:
            # Bounded EXTENT also handles subpixel marker estimates at screenshot edges.
            aligned = image.convert('RGB').transform((WIDTH, HEIGHT), Image.Transform.EXTENT, bounds, Image.Resampling.BILINEAR)
            yield read_aligned(aligned)
        except ValueError:
            continue
