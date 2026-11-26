"""Run on an unlocked Windows test desktop: python input_smoke.py path/to/DeskCal.exe

Uses a temporary WebView2 profile and the released DeskCal entry point. A test
observer only reads the DOM; clicks, wheel, typing and focus use Windows.
"""
import base64
import ctypes as C
from ctypes import wintypes as W
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import shutil
from threading import Thread
from queue import Queue

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
        env = dict(os.environ, WEBVIEW2_USER_DATA_FOLDER=profile)
        self.proc = subprocess.Popen([str(Path(exe).with_name("InputObserver.exe")), exe], env=env,
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     text=True, creationflags=subprocess.CREATE_NO_WINDOW)
        self.replies = Queue()
        def read_output():
            for line in self.proc.stdout:
                self.replies.put(line.strip())
            self.replies.put("EOF")
        Thread(target=read_output, daemon=True).start()
        self.hwnd = None
        try:
            self.hwnd = wait(lambda: find(None, "DeskCal 1.0.3"), "window", 30)
            # Start the dedicated input run on a visible widget even when a
            # provisioning window holds the foreground lock at runner startup.
            key(0x12, 0, 0, 0)
            key(0x12, 0, 2, 0)
            foreground(self.hwnd)
            self.until("document.querySelectorAll('.cell').length===42")
        except BaseException:
            print("Process", self.proc.poll(), "windows", window_texts(self.proc.pid), flush=True)
            self.close()
            raise

    def js(self, expression):
        self.proc.stdin.write(base64.b64encode(expression.encode()).decode() + "\n")
        self.proc.stdin.flush()
        line = self.replies.get(timeout=30)
        if line in ("EOF", "ERR"):
            raise RuntimeError("Observer returned " + line)
        return json.loads(base64.b64decode(line))

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
        target = root(at(point), 2)
        if target != self.hwnd:
            title = C.create_unicode_buffer(256)
            cls = C.create_unicode_buffer(256)
            u.GetWindowTextW(target, title, len(title))
            u.GetClassNameW(target, cls, len(cls))
            raise AssertionError(f"{selector} at {point.x},{point.y} targets {ascii(title.value)} "
                                 f"({cls.value}); widget origin {corner.x},{corner.y}, "
                                 f"client {size.right}x{size.bottom}, CSS {x},{y}/{height}, dpr {dpi}, "
                                 f"foreground {active()}, target {target}, widget {self.hwnd}")
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
        if self.hwnd:
            post(self.hwnd, 0x10, 0, 0)
        try:
            self.proc.wait(timeout=8 if self.hwnd else 1)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait()


def capture(hwnd):
    pos, bounds = W.POINT(), W.RECT()
    assert origin(hwnd, C.byref(pos)) and rect(hwnd, C.byref(bounds))
    # Only capture the empty test calendar's client area on the hosted runner.
    script = f"""
      Add-Type -AssemblyName System.Drawing
      $bmp = New-Object Drawing.Bitmap({bounds.right},{bounds.bottom})
      $g = [Drawing.Graphics]::FromImage($bmp)
      try {{
        $g.CopyFromScreen({pos.x},{pos.y},0,0,$bmp.Size)
        $bmp.Save((Join-Path $env:RUNNER_TEMP 'DeskCal.png'),[Drawing.Imaging.ImageFormat]::Png)
      }} finally {{ $g.Dispose(); $bmp.Dispose() }}
    """
    subprocess.run(["powershell", "-NoProfile", "-Command", script],
                   creationflags=subprocess.CREATE_NO_WINDOW, check=True)


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
        if os.environ.get("GITHUB_ACTIONS") == "true":
            capture(app.hwnd)
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
            foreground(probe)
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
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as workspace:
        source = Path(sys.argv[1]).resolve()
        app_dir = Path(workspace) / "app"
        shutil.copytree(source.parent, app_dir)
        profile = Path(workspace) / "profile"
        profile.mkdir()
        framework = Path(os.environ["SystemRoot"]) / "Microsoft.NET" / "Framework64" / "v4.0.30319"
        wpf = framework / "WPF"
        tests = Path(__file__).resolve().parents[2] / "tests" / "windows"
        observer = app_dir / "InputObserver.exe"
        subprocess.run([str(framework / "csc.exe"), "/nologo", "/platform:x64", "/target:exe",
                        "/out:" + str(observer),
                        "/win32manifest:" + str(Path(__file__).resolve().parents[1] / "app.manifest"),
                        *["/r:" + str(wpf / (name + ".dll")) for name in
                          ("WindowsBase", "PresentationCore", "PresentationFramework")],
                        "/r:" + str(framework / "System.Xaml.dll"),
                        *["/r:" + str(app_dir / ("Microsoft.Web.WebView2." + name + ".dll"))
                          for name in ("Core", "Wpf")], str(tests / "InputObserver.cs")], check=True)
        shutil.copyfile(str(app_dir / source.name) + ".config", str(observer) + ".config")
        run(str(app_dir / source.name), str(profile))
