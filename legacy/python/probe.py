"""Measure whether an actual RDP screenshot preserves native RGB bytes."""
import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image

from desktop import physical_pixels, resolve_size
from encode import play
import rgbframe


def measure(image):
    best = None
    for raw, size in rgbframe.read_frames(image):
        expected = np.frombuffer(rgbframe.probe_data(size), dtype=np.uint8)
        actual = np.frombuffer(raw, dtype=np.uint8)
        difference = actual != expected
        changed = int(difference.sum())
        result = dict(width=size[0], height=size[1], channels=expected.size, changed_channels=changed,
                      pixels=expected.size // 3, changed_pixels=int(difference.reshape(-1, 3).any(axis=1).sum()),
                      max_error=int(np.abs(actual.astype(np.int16) - expected.astype(np.int16)).max()))
        if best is None or changed < best['changed_channels']:
            best = result
    return best


def main():
    physical_pixels()
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    create = commands.add_parser('create', help='在遠端產生並顯示校驗圖')
    create.add_argument('--out', type=Path, default=Path('rdp-probe.png'))
    create.add_argument('--show', action='store_true')
    create.add_argument('--size', default='1600x900', help='RGB 尺寸，例如 1280x720 或 auto')
    create.add_argument('--fullscreen', action='store_true')
    check = commands.add_parser('check', help='在本地檢查實際 RDP 截圖')
    check.add_argument('screenshot', type=Path)
    args = parser.parse_args()
    if args.command == 'create':
        frame_size = resolve_size(args.size)
        with args.out.open('xb') as output:
            rgbframe.render(rgbframe.probe_data(frame_size), frame_size).save(output, format='PNG')
        print(f'已建立 {args.out}；在 RDP 內以原尺寸顯示，再從本地截圖。')
        if args.show:
            play([args.out], 0.5, 'rgb', args.fullscreen)
        return 0
    with Image.open(args.screenshot) as source:
        result = measure(source.convert('RGB'))
    if result is None:
        print('找不到原尺寸三點定位圖像。請確認 RDP 顯示為 1:1，沒有縮放或裁掉定位點。')
        return 2
    print(f"辨識尺寸：{result['width']} × {result['height']}")
    print(f"RGB 通道不符：{result['changed_channels']:,}/{result['channels']:,}")
    print(f"受影響像素：{result['changed_pixels']:,}/{result['pixels']:,}；最大通道差值：{result['max_error']}")
    if result['changed_channels'] == 0:
        print('這次截圖通過逐位元組校驗。請再測數次；此結果不保證後續 RDP 畫面完全無損。')
        return 0
    print('這次截圖未通過 RGB 無損條件；先調整 RDP 顯示／畫質設定，再重測。')
    return 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (ValueError, OSError) as exc:
        print(f'錯誤：{exc}', file=sys.stderr)
        sys.exit(1)
