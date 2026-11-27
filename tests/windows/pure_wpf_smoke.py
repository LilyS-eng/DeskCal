"""Exercise the diagnostic executable with no browser or in-process test host."""
import ctypes as C
from ctypes import wintypes as W
import os
from pathlib import Path
import subprocess
import sys
import time

u = C.windll.user32
u.SetProcessDpiAwarenessContext(-4)
u.FindWindowW.restype = W.HWND
cb = C.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)
u.PostMessageW.argtypes = [W.HWND, W.UINT, W.WPARAM, W.LPARAM]
dwm = C.windll.dwmapi.DwmGetWindowAttribute
dwm.argtypes = [W.HWND, W.DWORD, C.c_void_p, W.DWORD]


def wait(fn):
    stop = time.monotonic() + 20
    while time.monotonic() < stop:
        result = fn()
        if result:
            return result
        time.sleep(.08)
    raise AssertionError(fn)


def shown(h):
    cloak = W.DWORD()
    return u.IsWindowVisible(h) and dwm(h, 14, C.byref(cloak), 4) == 0 and cloak.value == 0


def click(x, y):
    u.SetCursorPos(x, y)
    u.mouse_event(2, 0, 0, 0, 0)
    u.mouse_event(4, 0, 0, 0, 0)


def bounds(h):
    r = W.RECT()
    assert u.GetWindowRect(h, C.byref(r))
    return r


def tray_windows(pid):
    handles = []
    @cb
    def collect(h, _):
        owner = W.DWORD()
        u.GetWindowThreadProcessId(h, C.byref(owner))
        name = C.create_unicode_buffer(128)
        u.GetClassNameW(h, name, len(name))
        if owner.value == pid and name.value.startswith('WindowsForms10.'):
            handles.append(h)
        return True
    u.EnumWindows(collect, 0)
    return handles


p = subprocess.Popen([sys.argv[1]])
try:
    h = wait(lambda: u.FindWindowW(None, 'DeskCal Acrylic B (WPF only)'))
    wait(lambda: shown(h))
    log = wait(lambda: next((Path(os.environ['LOCALAPPDATA'])/'DeskCal'/'logs').glob(
        f'acrylic-no-webview2-*-{p.pid}.log'), None))
    def records():
        return log.read_text(encoding='utf-8')
    wait(lambda: "DwmSet attr=13 requested=0 hr=00000000" in records())
    for cycle in range(2):
        r = bounds(h)
        u.SetForegroundWindow(h)
        u.SetCursorPos(r.right-18, r.bottom-18)
        u.mouse_event(2, 0, 0, 0, 0)
        wait(lambda: records().count("WPF grip down") > cycle)
        for delta in range(4, 24, 4):
            u.SetCursorPos(r.right-18-delta, r.bottom-18-delta)
            time.sleep(.06)
        u.mouse_event(4, 0, 0, 0, 0)
        wait(lambda: bounds(h).right < r.right-5)
        r = bounds(h)
        click(r.right-18, r.top+14)
        wait(lambda: not u.IsWindowVisible(h))
        # WinForms NotifyIcon callback (WM_USER+1024). Deliver a tray left click.
        for t in tray_windows(p.pid):
            for msg in (0x201, 0x202):
                u.PostMessageW(t, 0x800, 0, msg)
        wait(lambda: shown(h))
    for vk in (0x11, 0x12, ord('2')):
        u.keybd_event(vk, 0, 0, 0)
    for vk in (ord('2'), 0x12, 0x11):
        u.keybd_event(vk, 0, 2, 0)
    wait(lambda: 'MARK BEFORE_MANUAL_RESIZE browserModules=[]' in records())
    text = records()
    assert text.count('WPF grip down') == 2 and text.count('after Show') >= 2
    assert 'attr38=00000000/3' in text and 'pure WPF surface ready browserModules=[]' in text
    assert 'child Chrome_' not in text and 'snapshotError' not in text
    print('PASS: cold reveal, physical resize, close, tray callback and restore twice, marker and module census.')
except Exception:
    if 'log' in globals():
        print("\n".join(line.split(" | ")[0] for line in records().splitlines()[-40:]))
    raise
finally:
    if p.poll() is None:
        p.terminate()
    p.wait()
