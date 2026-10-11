"""Request physical pixel coordinates before creating windows on Windows."""
import sys


def physical_pixels():
    if sys.platform != 'win32':
        return
    import ctypes
    try:
        if ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
            return
    except (AttributeError, OSError):
        pass
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except (AttributeError, OSError):
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except (AttributeError, OSError):
            pass


def work_area(root):
    if sys.platform == 'win32':
        import ctypes
        from ctypes import wintypes
        rect = wintypes.RECT()
        if ctypes.windll.user32.SystemParametersInfoW(0x0030, 0, ctypes.byref(rect), 0):
            return rect.right - rect.left, rect.bottom - rect.top
    return root.winfo_screenwidth(), root.winfo_screenheight()


def fit_size(area):
    # Reserve window chrome and a status row. Keep 16:9 and cap at 1600x900.
    width, height = area
    units = min((width - 80) // 16, (height - 120) // 9, 100)
    if units < 40:
        raise ValueError('可用空間不足；RGB 模式至少需要容納 640 × 360 圖像及視窗邊框')
    return 16 * units, 9 * units


def resolve_size(value):
    if value.lower() == 'auto':
        import tkinter as tk
        try:
            root = tk.Tk()
            root.withdraw()
            try:
                return fit_size(work_area(root))
            finally:
                root.destroy()
        except tk.TclError as exc:
            raise ValueError('無法取得桌面尺寸，請明確指定 --size，例如 1280x720') from exc
    from rgbframe import validate_size
    try:
        width, height = map(int, value.lower().split('x'))
    except ValueError as exc:
        raise ValueError('--size 使用 WIDTHxHEIGHT，例如 1600x900，或 auto') from exc
    return validate_size((width, height))
