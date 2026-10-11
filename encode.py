"""Encrypt a file, generate PNG frames, optionally play them in a loop."""
import argparse
import getpass
import sys
from pathlib import Path

from transfer import MAX_FILE, encrypt, write_frames
from desktop import physical_pixels, resolve_size, work_area


def play(paths, fps, mode='color16', fullscreen=False):
    import tkinter as tk
    from PIL import Image, ImageTk
    root = tk.Tk()
    screen = (root.winfo_screenwidth(), root.winfo_screenheight()) if fullscreen else work_area(root)
    usable = screen if fullscreen else (screen[0] - 32, screen[1] - 96)
    with Image.open(paths[0]) as first:
        frame_size = first.size
    if mode == 'rgb' and (usable[0] < frame_size[0] or usable[1] < frame_size[1]):
        root.destroy()
        raise ValueError('圖像加上視窗邊框超過可用空間；請用 --size auto 或較小尺寸重新產生，或使用 --fullscreen')
    root.title('Pixel Transfer — Esc 關閉')
    root.attributes('-fullscreen', fullscreen)
    root.resizable(False, False)
    root.configure(bg='white')
    label = tk.Label(root, borderwidth=0, highlightthickness=0, padx=0, pady=0, bg='white')
    label.pack(expand=True)
    status = tk.Label(root)
    if not fullscreen or mode != 'rgb':
        status.pack()
    root.bind('<Escape>', lambda event: root.destroy())

    def advance(index=0):
        with Image.open(paths[index]) as im:
            fit = 1.0 if mode == 'rgb' else min(usable[0] / im.width,
                      (usable[1] - 32) / im.height, 1.0)
            if fit < 1:
                im = im.resize((round(im.width * fit), round(im.height * fit)), Image.Resampling.BILINEAR)
            photo = ImageTk.PhotoImage(im)
        label.configure(image=photo)
        label.image = photo
        status.configure(text=f'{index + 1} / {len(paths)} — {frame_size[0]} × {frame_size[1]}；Esc 結束')
        root.after(round(1000 / fps), advance, (index + 1) % len(paths))
    advance()
    root.mainloop()


def main():
    physical_pixels()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('file', type=Path)
    parser.add_argument('--out', type=Path, required=True, help='新的輸出資料夾')
    parser.add_argument('--play', action='store_true')
    parser.add_argument('--mode', choices=('color16', 'rgb'), default='color16')
    parser.add_argument('--size', default='1600x900', help='RGB 圖像尺寸，例如 1280x720、1600x900 或 auto')
    parser.add_argument('--fullscreen', action='store_true', help='全螢幕播放；預設使用一般視窗')
    parser.add_argument('--fps', type=float, default=1.0)
    args = parser.parse_args()
    frame_size = resolve_size(args.size) if args.mode == 'rgb' else (1920, 1080)
    if not 0.1 <= args.fps <= 10:
        parser.error('--fps 必須介於 0.1 與 10')
    if args.out.exists():
        parser.error('--out 必須是尚不存在的資料夾')
    with args.file.open('rb') as source:
        data = source.read(MAX_FILE + 1)
    if len(data) > MAX_FILE:
        parser.error('原型最多支援 64 MiB')
    password = getpass.getpass('加密密碼：')
    if password != getpass.getpass('再次輸入：'):
        raise ValueError('兩次密碼不同')
    envelope = encrypt(data, password)
    paths = write_frames(envelope, args.out, args.mode, frame_size)
    print(f'原始 {len(data):,} bytes → 壓縮及加密後 {len(envelope):,} bytes')
    print(f'{len(paths)} 張圖；每張 {frame_size[0]} × {frame_size[1]} 像素，模式 {args.mode}')
    print(f'以 {args.fps:g} fps 播放一輪至少 {len(paths) / args.fps:.1f} 秒；漏讀時繼續下一輪。')
    if args.play:
        play(paths, args.fps, args.mode, args.fullscreen)


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError) as exc:
        print(f'錯誤：{exc}', file=sys.stderr)
        sys.exit(1)
