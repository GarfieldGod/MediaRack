#!/usr/bin/env python
"""
给指定标题的窗口点一下鼠标（验收用：界面交互没法只靠 ViewModel 断言）。

用法：
    python click.py <窗口标题> <x> <y>          [--double] [--right]   # 相对窗口外框的物理像素
    python click.py <窗口标题> <rx> <ry> --rel  [--double] [--right]   # 比例（0~1）

比例模式更可靠：截出来的图在聊天里会被缩放显示，按"图上的像素"量坐标要乘一个
每次都不一样缩放系数，量三次能错两次。按比例量（x/图宽, y/图高）就不用管缩放。

注意：这是真鼠标事件（SetCursorPos + mouse_event），会移动用户的指针。
自动化跑完记得别留着窗口在等点击。
"""

import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.windll.user32

SW_RESTORE = 9
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010


def find_window(title: str) -> int:
    """按标题找顶层窗口。精确匹配优先，找不到再退回"标题包含"（Avalonia 的标题可能带后缀）。"""
    hwnd = user32.FindWindowW(None, title)
    if hwnd:
        return hwnd

    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def enum_proc(h, _):
        if not user32.IsWindowVisible(h):
            return True
        n = user32.GetWindowTextLengthW(h)
        if n <= 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 2)
        user32.GetWindowTextW(h, buf, n + 2)
        if title.lower() in buf.value.lower():
            found.append(h)
        return True

    user32.EnumWindows(enum_proc, 0)
    return found[0] if found else 0


def main() -> int:
    if len(sys.argv) < 4:
        print(__doc__)
        return 2

    # 与 screenshot.py 同一个理由：不声明 DPI 感知的话，GetWindowRect 拿到的是虚拟化坐标，
    # 点在 150% 缩放的屏上会偏到别的地方去。
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        user32.SetProcessDPIAware()

    title = sys.argv[1]
    double = "--double" in sys.argv
    right = "--right" in sys.argv
    relative = "--rel" in sys.argv

    hwnd = find_window(title)
    if not hwnd:
        print(f"找不到标题含「{title}」的窗口")
        return 1

    user32.ShowWindow(hwnd, SW_RESTORE)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.35)

    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w, h = rect.right - rect.left, rect.bottom - rect.top

    if relative:
        x = int(float(sys.argv[2]) * w)
        y = int(float(sys.argv[3]) * h)
    else:
        x, y = int(sys.argv[2]), int(sys.argv[3])

    sx, sy = rect.left + x, rect.top + y
    user32.SetCursorPos(sx, sy)
    time.sleep(0.12)

    down = MOUSEEVENTF_RIGHTDOWN if right else MOUSEEVENTF_LEFTDOWN
    up = MOUSEEVENTF_RIGHTUP if right else MOUSEEVENTF_LEFTUP

    def click() -> None:
        user32.mouse_event(down, 0, 0, 0, 0)
        time.sleep(0.05)
        user32.mouse_event(up, 0, 0, 0, 0)

    click()
    if double:
        time.sleep(0.08)
        click()

    how = f"比例({sys.argv[2]},{sys.argv[3]})" if relative else "像素"
    print(f"已点击窗口「{title}」{how} → 窗口内 ({x},{y})，窗口 {w}×{h}，屏幕 ({sx},{sy})"
          f"{'（双击）' if double else ''}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
