"""Run on an unlocked Windows test desktop: python input_smoke.py path/to/DeskCal.exe

Requires websocket-client. Uses a temporary WebView2 profile. CDP only inspects
the document and locates controls; clicks, wheel, typing and focus use Windows.
"""
import ctypes as C
from ctypes import wintypes as W
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
from urllib.request import ProxyHandler, build_opener

import websocket

u = C.windll.user32


def api(name, result, *args):
    fn = getattr(u, name)
    fn.argtypes, fn.restype = args, result
    return fn


hwnd_t = W.HWND
find = api("FindWindowW", hwnd_t, W.LPCWSTR, W.LPCWSTR)
at = api("WindowFromPoint", hwnd_t, W.POINT)
root = api("GetAncestor", hwnd_t, hwnd_t, W.UINT)
origin = api("ClientToScreen", W.BOOL, hwnd_t, C.POINTER(W.POINT))
rect = api("GetClientRect", W.BOOL, hwnd_t, C.POINTER(W.RECT))
style = api("GetWindowLongW", W.LONG, hwnd_t, C.c_int)
foreground = api("SetForegroundWindow", W.BOOL, hwnd_t)
active = api("GetForegroundWindow", hwnd_t)
post = api("PostMessageW", W.BOOL, hwnd_t, W.UINT, W.WPARAM, W.LPARAM)
move = api("SetCursorPos", W.BOOL, C.c_int, C.c_int)
mouse = api("mouse_event", None, W.DWORD, W.DWORD, W.DWORD, W.DWORD, C.c_size_t)
key = api("keybd_event", None, W.BYTE, W.BYTE, W.DWORD, C.c_size_t)
make_window = api("CreateWindowExW", hwnd_t, W.DWORD, W.LPCWSTR, W.LPCWSTR, W.DWORD,
                  C.c_int, C.c_int, C.c_int, C.c_int, hwnd_t, W.HMENU, W.HINSTANCE, W.LPVOID)
destroy = api("DestroyWindow", W.BOOL, hwnd_t)
api("SetProcessDpiAwarenessContext", W.BOOL, W.HANDLE)(-4)
get_material = C.windll.dwmapi.DwmGetWindowAttribute
get_material.argtypes = [hwnd_t, W.DWORD, C.c_void_p, W.DWORD]
get_material.restype = C.c_long


def window_texts(pid):
    lines = []
    callback = C.WINFUNCTYPE(W.BOOL, hwnd_t, W.LPARAM)
    @callback
    def capture(hwnd, unused):
        owner = W.DWORD()
        u.GetWindowThreadProcessId(hwnd, C.byref(owner))
        if owner.value == pid:
            caption = C.create_unicode_buffer(2048)
            u.GetWindowTextW(hwnd, caption, len(caption))
            if caption.value:
                lines.append(ascii(caption.value))
        return True
    @callback
    def top(hwnd, unused):
        capture(hwnd, 0)
        u.EnumChildWindows(hwnd, capture, 0)
        return True
    u.EnumWindows(top, 0)
    return lines


def wait(fn, label, seconds=15):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = fn()
        if value:
            return value
        time.sleep(.1)
    raise AssertionError("Timed out: " + label)


class Widget:
    def __init__(self, exe, profile):
        port_file = Path(profile) / "EBWebView" / "DevToolsActivePort"
        port_file.unlink(missing_ok=True)
        env = dict(os.environ, WEBVIEW2_USER_DATA_FOLDER=profile,
                   WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=0 --enable-logging")
        self.proc = subprocess.Popen([exe], env=env)
        self.ws = None
        self.hwnd = None
        targets = set()
        opener = build_opener(ProxyHandler({}))
        try:
            def page():
                if self.proc.poll() is not None:
                    raise RuntimeError("DeskCal exited before loading")
                try:
                    port = int(port_file.read_text().splitlines()[0])
                    with opener.open(f"http://127.0.0.1:{port}/json/list", timeout=2) as response:
                        pages = json.load(response)
                        targets.update(x.get("url", "") for x in pages)
                        return next((x for x in pages
                                     if x["url"].startswith("https://deskcal.invalid/index.html")), None)
                except (OSError, ValueError, IndexError):
                    return None
            target = wait(page, "WebView2 calendar", 90)
            self.ws = websocket.create_connection(target["webSocketDebuggerUrl"], suppress_origin=True, timeout=8)
            self.seq = 0
            self.hwnd = wait(lambda: find(None, "DeskCal 1.0.3"), "window")
            foreground(self.hwnd)
            self.until("document.querySelectorAll('.cell').length===42")
        except BaseException:
            print("Process", self.proc.poll(), "windows", window_texts(self.proc.pid),
                  "CDP pages", targets, "profile files", os.listdir(profile), flush=True)
            for log in Path(profile).rglob("chrome_debug.log"):
                print(log.read_text(errors="replace")[-3500:], flush=True)
            subprocess.run(["powershell", "-NoProfile", "-Command",
                "Get-Process explorer,DeskCal,msedgewebview2 -ErrorAction SilentlyContinue | "
                "Format-Table ProcessName,Id,SessionId,MainWindowTitle; "
                "Get-CimInstance Win32_Process -Filter \"Name='msedgewebview2.exe'\" | "
                "Where-Object { $_.CommandLine -notmatch '--type=' } | Select-Object -ExpandProperty CommandLine"])
            self.close()
            raise

    def js(self, expression):
        self.seq += 1
        self.ws.send(json.dumps({"id": self.seq, "method": "Runtime.evaluate",
                                 "params": {"expression": expression, "returnByValue": True}}))
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("id") == self.seq:
                result = msg["result"]
                assert "exceptionDetails" not in result, result
                return result["result"].get("value")

    def until(self, condition):
        return wait(lambda: self.js(condition), condition)

    def point(self, selector):
        code = """(s=>{const e=document.querySelector(s);if(!e)return null;
          const r=e.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2;
          if(!e.contains(document.elementFromPoint(x,y)))return null;
          return [x,y,devicePixelRatio,innerHeight];})(%s)""" % json.dumps(selector)
        x, y, dpi, height = wait(lambda: self.js(code), "visible " + selector)
        corner, size = W.POINT(), W.RECT()
        assert origin(self.hwnd, C.byref(corner)) and rect(self.hwnd, C.byref(size))
        point = W.POINT(round(corner.x + x*dpi), round(corner.y + size.bottom - (height-y)*dpi))
        assert root(at(point), 2) == self.hwnd, "Another HWND receives the pointer at " + selector
        return point

    def click(self, selector):
        pt = self.point(selector)
        assert move(pt.x, pt.y)
        mouse(2, 0, 0, 0, 0)
        mouse(4, 0, 0, 0, 0)
        time.sleep(.22)

    def wheel(self, x, y, delta):
        assert move(x, y)
        mouse(0x800, 0, 0, delta & 0xffffffff, 0)
        time.sleep(.35)

    def close(self):
        if self.ws:
            self.ws.close()
        if self.hwnd:
            post(self.hwnd, 0x10, 0, 0)
        try:
            self.proc.wait(timeout=8 if self.hwnd else 1)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait()


def type_letters(word):
    for letter in word:
        vk = ord(letter.upper())
        key(vk, 0, 0, 0)
        key(vk, 0, 2, 0)


def run(exe, profile):
    current = "JSON.parse(localStorage.getItem('deskcal-personal-v1')||'{\"todos\":[]}').todos"
    app = Widget(exe, profile)
    try:
        assert app.js(current + ".length") == 0
        ex = style(app.hwnd, -20)
        assert ex & 0x80 and not ex & (0x40000 | 0x20 | 0x08000000 | 0x80000 | 0x8), hex(ex)
        material = C.c_int()
        assert get_material(app.hwnd, 38, C.byref(material), C.sizeof(material)) == 0
        assert material.value == 3, f"Expected Desktop Acrylic, got {material.value}"
        print("Initial profile empty; Acrylic, tool-window and normal input styles verified.", flush=True)
        month = app.js("document.querySelector('#title-main').textContent")
        app.click("#next")
        assert app.js("document.querySelector('#title-main').textContent") != month
        app.click("#prev")
        app.click("[data-v=week]")
        app.until("document.querySelectorAll('.week .col').length===7")
        app.click("[data-v=month]")
        app.click(".cell.today .d")
        app.until("!!document.querySelector('.day-hero')")
        app.click("[data-v=day]")
        app.click("#add")
        app.until("document.activeElement.id==='f-title'")
        type_letters("deskcal")
        app.click("#f-ok")
        app.until(current + "[0]?.title==='deskcal'")
        app.click(".todo .ck")
        app.click(".todo [data-edit]")
        app.click("#f-title")
        key(0x11, 0, 0, 0); type_letters("a"); key(0x11, 0, 2, 0)
        type_letters("revised")
        app.click("#f-ok")
        app.until(current + "[0]?.title==='revised' && " + current + "[0].done")
        app.click("#bak")
        app.click("[data-f=done]")
        app.until("document.querySelector('#bak-list').textContent.includes('revised')")
        app.click("#bak-close")
        # The probe owns only a small, disjoint patch of the test desktop.
        corner = W.POINT()
        origin(app.hwnd, C.byref(corner))
        x = 0 if corner.x >= 85 else u.GetSystemMetrics(0)-85
        probe = make_window(0, "STATIC", "DeskCal focus test", 0x90000000, x, 0, 80, 50,
                            None, None, None, None)
        assert probe
        try:
            move(x+15, 15); mouse(2, 0, 0, 0, 0); mouse(4, 0, 0, 0, 0)
            wait(lambda: active()==probe, "focus leaves widget")
            app.click("#gear")
            app.until("document.querySelector('#panel').classList.contains('open')")
        finally:
            destroy(probe)
        app.click("#r-blur")
        app.click("#r-blur")
        app.click("#s-hide")
        app.click("#s-hide")
        panel = app.point("#s-hide")
        app.wheel(panel.x, panel.y, -840)
        app.until("document.querySelector('#panel').scrollTop>0")
        app.click("#gear")
        down, clicks = app.js("window.deskcalInputStatus()")
        assert down>=18 and clicks>=18
        print(f"PASS: {down} OS pointer downs, {clicks} clicks, editor focus/typing, dialogs, views, scroll, focus return.", flush=True)
    finally:
        app.close()
    app = Widget(exe, profile)
    try:
        app.until(current + ".length===1 && " + current + "[0].title==='revised'")
        app.click("#bak")
        app.click("[data-bdel]")
        app.until(current + ".length===0")
        print("PASS: restart persistence and deletion.", flush=True)
    finally:
        app.close()


if __name__ == "__main__":
    if find(None, "DeskCal 1.0.3"):
        sys.exit("Exit the running copy before running this desktop test.")
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as profile:
        run(str(Path(sys.argv[1]).resolve()), profile)
