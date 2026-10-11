"""Detect three finder rings and infer quarter-turn orientation from their L."""
import itertools

import numpy as np
from PIL import Image
from scipy import ndimage


def candidates(image, native=False):
    factor = 1.0 if native else min(1.0, 1200 / max(image.size))
    small = image if native else image.resize(
        (round(image.width * factor), round(image.height * factor)), Image.Resampling.BILINEAR)
    rgb = np.asarray(small.convert('RGB'))
    labels, _ = ndimage.label(np.max(rgb, axis=2) < 28)
    found = []
    for number, region in enumerate(ndimage.find_objects(labels), 1):
        if region is None:
            continue
        ys, xs = region
        w, h = xs.stop - xs.start, ys.stop - ys.start
        if native:
            if w != 40 or h != 40:
                continue
        elif not (8 <= w <= 100 and 8 <= h <= 100 and 0.65 <= w / h <= 1.5):
            continue
        mask = labels[region] == number
        if not 0.40 < mask.mean() < 0.85:
            continue
        if mask[h // 3:2 * h // 3, w // 3:2 * w // 3].mean() > 0.15:
            continue
        found.append(((xs.start + xs.stop) / (2 * factor),
                      (ys.start + ys.stop) / (2 * factor), w / factor, h / factor))
    if len(found) > 24:
        raise ValueError('定位候選過多，請縮小擷取範圍')
    return found


def layouts(image, native=False, fixed_size=None):
    """Yield image bounds, CCW quarter-turns to normalize, and upright size.

Three rings occupy TL, TR and BL in the upright frame. The missing corner
identifies rotation. Legacy four-ring frames produce extra candidates; the
packet CRC/authentication (or probe comparison) selects the valid payload.
    """
    for group in itertools.combinations(candidates(image, native), 3):
        tolerance = 0 if native else max(3, min(group[0][2:]) * 0.15)
        for elbow, horizontal, vertical in itertools.permutations(group):
            if abs(elbow[1] - horizontal[1]) > tolerance or abs(elbow[0] - vertical[0]) > tolerance:
                continue
            dx, dy = horizontal[0] - elbow[0], vertical[1] - elbow[1]
            if abs(dx) < 150 or abs(dy) < 150:
                continue
            turns = {(True, True): 0, (False, True): 1,
                     (False, False): 2, (True, False): 3}[(dx > 0, dy > 0)]
            if fixed_size is None:
                sx = sy = 1.0
                physical_size = (round(abs(dx) + 72), round(abs(dy) + 72))
                upright_size = physical_size[::-1] if turns % 2 else physical_size
                if not (640 <= upright_size[0] <= 3840 and 360 <= upright_size[1] <= 2160):
                    continue
            else:
                physical_size = fixed_size[::-1] if turns % 2 else fixed_size
                sx, sy = abs(dx) / (physical_size[0] - 72), abs(dy) / (physical_size[1] - 72)
                upright_size = fixed_size
                if any(abs(p[2] / (40 * sx) - 1) > 0.25 or
                       abs(p[3] / (40 * sy) - 1) > 0.25 for p in group):
                    continue
            bounds = (min(elbow[0], horizontal[0]) - 36 * sx,
                      min(elbow[1], vertical[1]) - 36 * sy,
                      max(elbow[0], horizontal[0]) + 36 * sx,
                      max(elbow[1], vertical[1]) + 36 * sy)
            yield bounds, turns, upright_size


def upright(image, turns):
    if turns:
        operation = (Image.Transpose.ROTATE_90, Image.Transpose.ROTATE_180, Image.Transpose.ROTATE_270)[turns - 1]
        return image.transpose(operation)
    return image
