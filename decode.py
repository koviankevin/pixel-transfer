"""Read screenshots or capture a screen region, then decrypt the complete file."""
import argparse
import getpass
import sys
import time
from pathlib import Path

from PIL import Image, ImageGrab
from transfer import Collector, decrypt, scan
from desktop import physical_pixels


def region(value):
    try:
        left, top, width, height = map(int, value.split(','))
        if width <= 0 or height <= 0:
            raise ValueError()
        return dict(left=left, top=top, width=width, height=height)
    except ValueError:
        raise argparse.ArgumentTypeError('使用 x,y,width,height；寬高須大於零')


def select_region():
    import tkinter as tk
    root = tk.Tk()
    root.attributes('-fullscreen', True)
    root.attributes('-topmost', True)
    root.attributes('-alpha', 0.35)
    canvas = tk.Canvas(root, bg='gray', cursor='crosshair', highlightthickness=0)
    canvas.pack(fill='both', expand=True)
    canvas.create_text(30, 30, anchor='nw', fill='white',
                       text='拖曳框選完整傳輸圖像（包含四角）；Esc 取消', font=('', 20))
    start, result = [], {}
    rectangle = canvas.create_rectangle(0, 0, 0, 0, outline='red', width=4)

    def down(event):
        start[:] = [event.x_root, event.y_root, event.x, event.y]

    def drag(event):
        if start:
            canvas.coords(rectangle, start[2], start[3], event.x, event.y)

    def up(event):
        if start:
            width, height = abs(event.x_root - start[0]), abs(event.y_root - start[1])
            if width >= 100 and height >= 100:
                result.update(left=min(start[0], event.x_root), top=min(start[1], event.y_root),
                              width=width, height=height)
                root.destroy()

    canvas.bind('<ButtonPress-1>', down)
    canvas.bind('<B1-Motion>', drag)
    canvas.bind('<ButtonRelease-1>', up)
    root.bind('<Escape>', lambda event: root.destroy())
    root.mainloop()
    if not result:
        raise ValueError('已取消區域選取')
    return result


def main():
    physical_pixels()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('images', type=Path, nargs='?', help='截圖檔案或資料夾')
    parser.add_argument('--screen', type=region, help='持續擷取螢幕區域，例如 100,100,900,900')
    parser.add_argument('--select', action='store_true', help='用滑鼠框選擷取區域（主螢幕）')
    parser.add_argument('--mode', choices=('color16', 'rgb'), default='color16')
    parser.add_argument('--out', type=Path, required=True, help='還原檔案的完整路徑')
    parser.add_argument('--interval', type=float, default=0.15, help='每次辨識後等待秒數')
    args = parser.parse_args()
    if sum((args.images is not None, args.screen is not None, args.select)) != 1:
        parser.error('請選擇截圖路徑、--screen 或 --select 其中一種')
    if not 0.05 <= args.interval <= 60:
        parser.error('--interval 必須介於 0.05 與 60')
    if args.out.exists():
        parser.error('輸出檔案已存在，請使用新路徑')
    if args.select:
        args.screen = select_region()
    collector = Collector()

    def accept(image):
        previous = len(collector.parts)
        scan(image, collector, args.mode)
        if len(collector.parts) != previous:
            print(f'已收到 {len(collector.parts)}/{collector.total} 分片', flush=True)

    if args.screen:
        print('開始擷取，Ctrl+C 結束。請讓發送端循環播放。', flush=True)
        r = args.screen
        bounds = (r['left'], r['top'], r['left'] + r['width'], r['top'] + r['height'])
        while not collector.complete:
            accept(ImageGrab.grab(bbox=bounds))
            if not collector.complete:
                time.sleep(args.interval)
    else:
        paths = sorted(args.images.iterdir()) if args.images.is_dir() else [args.images]
        for path in paths:
            if path.suffix.lower() not in ('.png', '.jpg', '.jpeg', '.bmp', '.webp'):
                continue
            with Image.open(path) as im:
                accept(im.convert('RGB'))
    if not collector.complete:
        if collector.total:
            missing = [str(i + 1) for i in range(collector.total) if i not in collector.parts]
            print('缺少分片（從 1 起算）：' + ', '.join(missing[:30]))
        else:
            print('找不到可用的傳輸圖像；請確認模式，保留四角與白邊。RGB 模式要求像素完全不變。')
        return 2
    envelope = collector.assemble()
    password = getpass.getpass('解密密碼：')
    data = decrypt(envelope, password)
    # Exclusive creation prevents accidental overwrites, including a late race.
    with args.out.open('xb') as destination:
        destination.write(data)
    print(f'已驗證並還原 {len(data):,} bytes → {args.out}')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print('\n已停止。')
        sys.exit(130)
    except (ValueError, OSError) as exc:
        print(f'錯誤：{exc}', file=sys.stderr)
        sys.exit(1)
