#!/usr/bin/env python
"""
Windows 截屏 → PNG（纯标准库：ctypes + zlib，不装 Pillow 也能用）。

验收要用截图留证，而 PowerShell 的 Add-Type 在工作环境里被安全策略挡住，
所以走 ctypes 直接调 GDI：抓主屏 → 存 PNG。

用法：
    python screenshot.py <输出.png> [窗口标题]

给了窗口标题就先把它提到前台再截（避免截到被别的窗口盖住的画面）。
"""

import ctypes
import struct
import sys
import time
import zlib
from ctypes import wintypes

SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0
BI_RGB = 0


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


def capture(width: int, height: int) -> bytes:
    """抓主屏，返回 BGRA 像素（自下而上）。"""
    user32 = ctypes.windll.user32
    gdi32 = ctypes.windll.gdi32

    hdc = user32.GetDC(0)
    memdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, width, height)
    gdi32.SelectObject(memdc, bmp)
    gdi32.BitBlt(memdc, 0, 0, width, height, hdc, 0, 0, SRCCOPY)

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = width
    bi.biHeight = -height  # 负值 = 自上而下，省一次翻转
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = BI_RGB

    stride = width * 4
    buf = ctypes.create_string_buffer(stride * height)
    got = gdi32.GetDIBits(memdc, bmp, 0, height, buf, ctypes.byref(bi), DIB_RGB_COLORS)
    if got != height:
        raise RuntimeError(f"GetDIBits 只拿到 {got}/{height} 行")

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(memdc)
    user32.ReleaseDC(0, hdc)
    return buf.raw


def write_png(path: str, width: int, height: int, bgra: bytes) -> None:
    """BGRA → PNG。手写容器，避免依赖 Pillow。"""
    stride = width * 4
    raw = bytearray()
    for y in range(height):
        row = bgra[y * stride:(y + 1) * stride]
        raw.append(0)  # 过滤类型 0
        # BGRA → RGB
        raw += bytes(bytearray(
            b for i in range(width)
            for b in (row[i * 4 + 2], row[i * 4 + 1], row[i * 4])
        ))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 6))
    png += chunk(b"IEND", b"")

    with open(path, "wb") as f:
        f.write(png)


def capture_rect(left: int, top: int, width: int, height: int) -> bytes:
    """抓屏幕上一块矩形（物理像素），返回 BGRA（自上而下）。"""
    user32 = ctypes.windll.user32
    gdi32 = ctypes.windll.gdi32

    hdc = user32.GetDC(0)
    memdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, width, height)
    gdi32.SelectObject(memdc, bmp)
    gdi32.BitBlt(memdc, 0, 0, width, height, hdc, left, top, SRCCOPY)

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = width
    bi.biHeight = -height
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = BI_RGB

    stride = width * 4
    buf = ctypes.create_string_buffer(stride * height)
    got = gdi32.GetDIBits(memdc, bmp, 0, height, buf, ctypes.byref(bi), DIB_RGB_COLORS)
    if got != height:
        raise RuntimeError(f"GetDIBits 只拿到 {got}/{height} 行")

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(memdc)
    user32.ReleaseDC(0, hdc)
    return buf.raw


def window_rect(hwnd) -> tuple:
    """窗口在屏幕上的物理像素矩形 (left, top, right, bottom)。"""
    r = wintypes.RECT()
    ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def main() -> int:
    # 用法：
    #   screenshot.py <out.png> [窗口标题]            整屏
    #   screenshot.py <out.png> [窗口标题] --window   只截该窗口（精确矩形，物理像素）
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    out = sys.argv[1]
    rest = [a for a in sys.argv[2:] if not a.startswith("--")]
    window_only = "--window" in sys.argv
    title = rest[0] if rest else None

    user32 = ctypes.windll.user32
    # 进程 DPI 感知：否则 GetWindowRect 拿的是逻辑坐标，和屏幕物理像素对不上
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PER_MONITOR_DPI_AWARE
    except Exception:
        user32.SetProcessDPIAware()

    hwnd = None
    if title:
        hwnd = user32.FindWindowW(None, title)
        if hwnd:
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.6)

    if window_only and hwnd:
        left, top, right, bottom = window_rect(hwnd)
        width, height = right - left, bottom - top
        write_png(out, width, height, capture_rect(left, top, width, height))
        print(f"已截图（窗口 {title}）{width}x{height} @({left},{top}) → {out}")
        return 0

    width = user32.GetSystemMetrics(0)
    height = user32.GetSystemMetrics(1)
    write_png(out, width, height, capture(width, height))
    print(f"已截图 {width}x{height} → {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
