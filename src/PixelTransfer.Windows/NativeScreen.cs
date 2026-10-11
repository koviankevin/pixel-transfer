using System.ComponentModel;
using System.Runtime.InteropServices;
using PixelTransfer.Core;

namespace PixelTransfer.Windows;

public readonly record struct CaptureArea(int X, int Y, int Width, int Height)
{
    public override string ToString() => $"({X}, {Y}) · {Width} × {Height} 像素";
}

internal static class NativeScreen
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important;
    }
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, out Rect rect, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint dst, int x, int y, int width, int height, nint src, int sx, int sy, uint op);

    public static CaptureArea WorkArea()
    {
        if (SystemParametersInfo(0x0030, 0, out var rect, 0)) return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return new(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
    }
    public static RgbImage Capture(CaptureArea area)
    {
        if (area.Width < 1 || area.Height < 1 || (long)area.Width * area.Height > 40_000_000) throw new InvalidOperationException("擷取區域過大或無效。");
        nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
        try
        {
            if (screen == 0) throw new Win32Exception();
            memory = CreateCompatibleDC(screen);
            if (memory == 0) throw new Win32Exception();
            var info = new BitmapInfo { Size = 40, Width = area.Width, Height = -area.Height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new Win32Exception();
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, 0x40CC0020)) throw new Win32Exception();
            byte[] bgra = new byte[checked(area.Width * area.Height * 4)];
            Marshal.Copy(bits, bgra, 0, bgra.Length);
            var result = new RgbImage(area.Width, area.Height);
            for (int p = 0, q = 0; p < bgra.Length; p += 4, q += 3)
            {
                result.Pixels[q] = bgra[p + 2]; result.Pixels[q + 1] = bgra[p + 1]; result.Pixels[q + 2] = bgra[p];
            }
            return result;
        }
        finally
        {
            if (previous != 0 && memory != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
}
