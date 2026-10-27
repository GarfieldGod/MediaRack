#!/usr/bin/env python
"""
给指定标题的窗口发一个按键（验收用：飞键盘快捷键不能靠手点）。

用法：
    python sendkey.py <窗口标题> <按键名 或 VK码>

按键名：space esc left right up down tab enter f11
        单字母键直接写字母（f / m / n / p / l / c / k …），大小写不敏感
VK 也可以直接给十六进制码：0x20
"""

import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.windll.user32

SW_RESTORE = 9
VK = {
    "space": 0x20, "esc": 0x1B, "tab": 0x09, "enter": 0x0D,
    "left": 0x25, "up": 0x26, "right": 0x27, "down": 0x28,
    "f11": 0x7A,
}


def resolve(key):
    """按键名 / 单字母 / 十六进制 VK 三种写法都认。"""
    k = key.strip().lower()
    if k in VK:
        return VK[k]
    if len(k) == 1 and "a" <= k <= "z":
        return ord(k.upper())          # 字母键的 VK 就是大写 ASCII
    return int(key, 0)


def find(title):
    """精确标题优先，退化为前缀匹配（Avalonia 有时会带上后缀）。"""
    hwnd = user32.FindWindowW(None, title)
    if hwnd:
        return hwnd

    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def enum_proc(h, _):
        n = user32.GetWindowTextLengthW(h)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(h, buf, n + 1)
            if buf.value.startswith(title) and user32.IsWindowVisible(h):
                found.append(h)
        return True

    user32.EnumWindows(enum_proc, 0)
    return found[0] if found else 0


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    title = sys.argv[1]
    key = sys.argv[2]
    vk = resolve(key)

    hwnd = find(title)
    if not hwnd:
        print(f"找不到窗口：{title}")
        return 1

    user32.ShowWindow(hwnd, SW_RESTORE)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.35)

    # ⚠️ 必须带扫描码：Avalonia 12 的 PhysicalKey 是从扫描码推出来的，
    # 只发 VK（scan=0）会让 IME 拦下的字母键变成 "ImeProcessed / PhysicalKey=None"，
    # 测试里表现为"按什么都没反应"，但真人按键是好的——纯测试工具造成的假故障。
    scan = user32.MapVirtualKeyW(vk, 0)
    user32.keybd_event(vk, scan, 0, 0)
    time.sleep(0.06)
    user32.keybd_event(vk, scan, 2, 0)
    print(f"已发送 {key} (vk=0x{vk:02X}, scan=0x{scan:02X}) → hwnd={hwnd}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
