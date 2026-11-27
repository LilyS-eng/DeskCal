"""Run on an unlocked Windows test desktop: python input_smoke.py path/to/DeskCal.exe

Uses a temporary WebView2 profile and the released DeskCal entry point. A test
observer only reads the DOM; clicks, wheel, typing and focus use Windows.
"""
import base64
import ctypes as C
from datetime import datetime, timedelta
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
window_rect = api("GetWindowRect", W.BOOL, hwnd_t, C.POINTER(W.RECT))
rect = api("GetClientRect", W.BOOL, hwnd_t, C.POINTER(W.RECT))
style = api("GetWindowLongW", W.LONG, hwnd_t, C.c_int)
foreground = api("SetForegroundWindow", W.BOOL, hwnd_t)
active = api("GetForegroundWindow", hwnd_t)
post = api("PostMessageW", W.BOOL, hwnd_t, W.UINT, W.WPARAM, W.LPARAM)
visible = api("IsWindowVisible", W.BOOL, hwnd_t)
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


def presented(hwnd):
    cloaked = C.c_int()
    return bool(hwnd and visible(hwnd) and
                get_material(hwnd, 14, C.byref(cloaked), C.sizeof(cloaked)) == 0 and cloaked.value == 0)


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
            self.hwnd = wait(lambda: find(None, "DeskCal 1.0.6"), "window", 120)
            wait(lambda: self.js("document.querySelectorAll('.cell').length===42"),
                 "WebView2 renders the initial calendar", 90)
            wait(lambda: presented(self.hwnd), "first composed window reveal", 30)
            foreground(self.hwnd)
        except BaseException:
            print("Process", self.proc.poll(), "windows", window_texts(self.proc.pid), flush=True)
            self.close()
            raise

    def send(self, expression):
        self.proc.stdin.write(base64.b64encode(expression.encode()).decode() + "\n")
        self.proc.stdin.flush()

    def js(self, expression):
        self.send(expression)
        line = self.replies.get(timeout=90)
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

    def close(self, verify=False):
        if self.proc.poll() is not None:
            assert not verify, "DeskCal quit before the tray exit action"
            return
        try:
            if self.hwnd:
                self.send("tray:exit")
            code = self.proc.wait(timeout=12 if self.hwnd else 1)
            if verify:
                assert code == 0, code
        except (OSError, subprocess.TimeoutExpired):
            self.proc.kill()
            self.proc.wait()
            if verify:
                raise


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


def assert_acrylic(hwnd):
    material = C.c_int()
    assert get_material(hwnd, 38, C.byref(material), C.sizeof(material)) == 0
    assert material.value == 3, f"Expected Desktop Acrylic, got {material.value}"


def window_geometry(hwnd):
    bounds = W.RECT()
    assert window_rect(hwnd, C.byref(bounds))
    return bounds.left, bounds.top, bounds.right, bounds.bottom


def screen_pixel(x, y):
    get_dc = api("GetDC", W.HDC, hwnd_t)
    release_dc = api("ReleaseDC", C.c_int, hwnd_t, W.HDC)
    pixel = C.windll.gdi32.GetPixel
    pixel.argtypes, pixel.restype = (W.HDC, C.c_int, C.c_int), W.DWORD
    dc = get_dc(None)
    assert dc
    try:
        color = pixel(dc, x, y)
        assert color != 0xffffffff
        return color
    finally:
        release_dc(None, dc)


def work_area():
    area = W.RECT()
    get_work = api("SystemParametersInfoW", W.BOOL, W.UINT, W.UINT, C.c_void_p, W.UINT)
    assert get_work(0x30, 0, C.byref(area), 0)
    return area


def mouse_click(x, y):
    assert move(x, y)
    mouse(2, 0, 0, 0, 0)
    mouse(4, 0, 0, 0, 0)


def drag_widget(app, target_left):
    left, top, right, _ = window_geometry(app.hwnd)
    x, y = (left+right)//2, top+12
    assert move(x, y)
    mouse(2, 0, 0, 0, 0)
    time.sleep(.2)
    offset = target_left-left
    for step in range(1, 9):
        assert move(x + round(offset*step/8), y)
        time.sleep(.04)
    mouse(4, 0, 0, 0, 0)


def test_edges(app):
    work = work_area()
    tab_caption = "DeskCal \u8fb9\u7f18\u628a\u624b"
    left, top, right, bottom = window_geometry(app.hwnd)
    width, height = right-left, bottom-top
    middle = (work.left+work.right-width)//2
    for target, side in [(work.left+4, "left"), (work.right-width-4, "right")]:
        drag_widget(app, target)
        tab = wait(lambda: find(None, tab_caption), "edge tab created")
        wait(lambda: visible(tab) and not visible(app.hwnd), "edge tab only")
        x1,y1,x2,y2 = window_geometry(tab)
        if side == "left":
            assert abs(x1-work.left) <= 2, (x1, x2)
        else:
            assert abs(x2-work.right) <= 2, (x1, x2)
        assert y2-y1 > 40 and x2-x1 > 8
        x,y = (x1+x2)//2, (y1+y2)//2
        wait(lambda: root(at(W.POINT(x,y)),2) == tab, f"{side} tab at {(x,y)} receives hit test")
        mouse_click(x,y)
        try:
            wait(lambda: presented(app.hwnd) and not visible(tab), "edge tab expands calendar")
        except AssertionError:
            print(f"{side} tab: shown={visible(tab)}, main shown={visible(app.hwnd)},"
                  f" presented={presented(app.hwnd)}, foreground={active()}",flush=True)
            raise
        expected_left = work.left if side=="left" else work.right-width
        assert window_geometry(app.hwnd) == (expected_left,top,expected_left+width,bottom)
        assert_acrylic(app.hwnd)
        app.click("#next")
        app.click("#prev")
        if side == "left":
            mouse_click(expected_left+width-55, top+15)
            wait(lambda: visible(tab) and not visible(app.hwnd), "header collapses docked calendar")
            app.js("tray:left")
            wait(lambda: presented(app.hwnd) and not visible(tab), "tray opens edge-hidden calendar")
            app.js("tray:left")
            wait(lambda: not visible(app.hwnd) and not visible(tab), "tray keeps both surfaces hidden")
            app.js("tray:left")
            wait(lambda: presented(app.hwnd), "tray restores docked calendar")
        drag_widget(app, middle)
        wait(lambda: abs(window_geometry(app.hwnd)[0]-middle) <= 3, "drag moves window away from edge")
        assert presented(app.hwnd) and not visible(tab)
    print("PASS: actual mouse left/right snap, click expand, collapse, undock and tray transitions.", flush=True)


def probe_backdrop(app):
    # The hosted display can force Acrylic fallback. Record compositor pixel
    # changes independently of the DWM attribute, without input to DeskCal.
    left, top, right, bottom = window_geometry(app.hwnd)
    points = [(left+105,top+10),(left+47,bottom-34)]
    try:
        app.js("backdrop:red")
        time.sleep(.35)
        red = [screen_pixel(x,y) for x,y in points]
        app.js("backdrop:blue")
        time.sleep(.35)
        blue = [screen_pixel(x,y) for x,y in points]
        assert window_geometry(app.hwnd)==(left,top,right,bottom)
        if red[1] != blue[1]:
            print("Backdrop probe: visible pixel below WebView2 changed without input or resize.", red, blue, flush=True)
        else:
            print("Backdrop probe: hosted compositor pixel result inconclusive.", red, blue, flush=True)
    finally:
        app.js("backdrop:off")


def drag_resize(app):
    left,top,right,bottom = window_geometry(app.hwnd)
    for vk in (0x11, 0x12, ord("2")):
        key(vk, 0, 0, 0)
    for vk in (ord("2"), 0x12, 0x11):
        key(vk, 0, 2, 0)
    point = app.point("#grip")
    assert move(point.x,point.y)
    mouse(2,0,0,0,0)
    time.sleep(.2)
    for step in range(1,6):
        assert move(point.x-6*step,point.y-4*step)
        time.sleep(.045)
    mouse(4,0,0,0,0)
    wait(lambda: window_geometry(app.hwnd)[2] < right-12, "browser drag updates window width")
    wait(lambda: window_geometry(app.hwnd)[3] < bottom-8, "browser drag updates window height")
    assert_acrylic(app.hwnd)
    app.until("Number.isInteger(S.settings.winWidth) && Number.isInteger(S.settings.winHeight)")
    print("PASS: actual pointer resize applies and saves both axes with Acrylic enabled.",flush=True)


def test_size(app):
    app.click("#gear")
    app.until("document.querySelector('#panel').classList.contains('open')")
    app.js("document.querySelector('#panel').scrollTop=0")
    original = window_geometry(app.hwnd)
    submit = "#window-size button"
    app.click("#win-width")
    key(0x11, 0, 0, 0); type_letters("a"); key(0x11, 0, 2, 0)
    type_letters("x")
    app.click(submit)
    assert window_geometry(app.hwnd) == original
    assert app.js("document.querySelector('#window-size-status').textContent")

    def enter(width, height):
        app.js(f"document.querySelector('#win-width').value={json.dumps(width)};"
               f"document.querySelector('#win-height').value={json.dumps(height)}")
        app.click(submit)

    for bad in ["", "-10", "9999999999"]:
        enter(bad, "510")
        assert window_geometry(app.hwnd) == original

    def stored(width,height):
        app.until(f"S.settings.winWidth==={width} && S.settings.winHeight==={height}")

    enter("1","2")
    stored(380,300)
    enter("999999999","999999999")
    ratio = app.js("devicePixelRatio")
    work = work_area()
    stored(int((work.right-work.left)/ratio),int((work.bottom-work.top)/ratio))
    assert_acrylic(app.hwnd)
    enter("640","510")
    stored(640,510)
    app.click("#gear")
    print("PASS: numeric keyboard, invalid/blank/negative/overflow input, min and current work area clamps.", flush=True)


def run(exe, profile):
    current = "JSON.parse(localStorage.getItem('deskcal-personal-v1')||'{\"todos\":[]}').todos"
    app = Widget(exe, profile)
    try:
        app.js("tray:diagnostic-ab")
        app.js("tray:diagnostic-ab")
        assert app.js(current + ".length") == 0
        ex = style(app.hwnd, -20)
        assert ex & 0x80 and not ex & (0x40000 | 0x20 | 0x08000000 | 0x80000 | 0x8), hex(ex)
        assert_acrylic(app.hwnd)
        print("Initial profile empty; Acrylic attribute enabled before resize, tool-window and input styles verified.", flush=True)
        if os.environ.get("GITHUB_ACTIONS") == "true":
            capture(app.hwnd)
        original = window_geometry(app.hwnd)
        corner, bounds = W.POINT(), W.RECT()
        assert origin(app.hwnd, C.byref(corner)) and rect(app.hwnd, C.byref(bounds))
        dpi = app.js("devicePixelRatio")
        # Click in the widened region below and to the left of the old button.
        close_x = corner.x + bounds.right - round(32 * dpi)
        close_y = corner.y + round(21 * dpi)
        assert move(close_x - round(70 * dpi), close_y)
        time.sleep(.2)
        idle_color = screen_pixel(close_x, close_y)
        assert move(close_x, close_y)
        wait(lambda: screen_pixel(close_x, close_y) != idle_color, "visible close button hover")
        for _ in range(5):
            assert move(close_x, close_y)
            mouse(2, 0, 0, 0, 0)
            mouse(4, 0, 0, 0, 0)
            wait(lambda: not visible(app.hwnd), "window close button hides DeskCal")
            assert app.proc.poll() is None
            app.js("tray:left")
            wait(lambda: presented(app.hwnd), "tray restores window")
            time.sleep(.3)
            assert window_geometry(app.hwnd) == original
            assert_acrylic(app.hwnd)
        app.js("tray:left")
        wait(lambda: not visible(app.hwnd), "tray click also hides")
        app.js("tray:left")
        wait(lambda: presented(app.hwnd), "tray click restores again")
        assert window_geometry(app.hwnd) == original
        assert_acrylic(app.hwnd)
        print("PASS: close hover and larger mouse target; six tray restores kept Acrylic enabled and the same bounds.", flush=True)
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
        probe_backdrop(app)
        drag_resize(app)
        test_size(app)
        test_edges(app)
    finally:
        app.close()
    assert app.proc.returncode == 0, "Tray exit must end the process normally"
    app = Widget(exe, profile)
    try:
        app.until(current + ".length===1 && " + current + "[0].title==='revised'")
        ratio = app.js("devicePixelRatio")
        left, top, right, bottom = window_geometry(app.hwnd)
        assert (right-left,bottom-top) == (round(640*ratio),round(510*ratio))
        app.click("#bak")
        app.click("[data-bdel]")
        app.until(current + ".length===0")
        print("PASS: restart preserves size and todos; deletion works.", flush=True)
    finally:
        app.close()


def test_reminders(exe, profile):
    caption = "DeskCal \u63d0\u9192"
    receipts = Path(profile) / "DeskCal-reminder-receipts.json"

    def due_after(minutes):
        return datetime.now().replace(second=0, microsecond=0) + timedelta(minutes=minutes)

    def set_time(app, due):
        app.js(f"document.querySelector('#f-date').value='{due:%Y-%m-%d}';"
               f"document.querySelector('#f-time').value='{due:%H:%M}'")
        app.click("#f-ok")

    def expect_popup(app, due, count):
        assert not find(None, caption), "Reminder visible before its scheduled minute"
        remaining = max(0, (due - datetime.now()).total_seconds())
        hwnd = wait(lambda: find(None, caption), caption, remaining + 16)
        # The HWND exists before the UI thread finishes atomically replacing
        # the receipt file. Wait for that dispatcher work before opening it.
        assert app.js("true")
        assert len(json.loads(receipts.read_text())) == count
        assert post(hwnd, 0x10, 0, 0)
        wait(lambda: not find(None, caption), "reminder dismissed")

    app = Widget(exe, profile)
    due = due_after(2)
    try:
        app.click("#add")
        app.until("document.activeElement.id==='f-title'")
        type_letters("reminder")
        set_time(app, due)
    finally:
        app.close(verify=True)

    app = Widget(exe, profile)
    try:
        app.js("tray:left")
        wait(lambda: not visible(app.hwnd), "hidden before scheduled time")
        expect_popup(app, due, 1)
        assert not visible(app.hwnd), "Reminder reopened the widget"
    finally:
        app.close(verify=True)

    app = Widget(exe, profile)
    try:
        time.sleep(6)
        assert not find(None, caption), "Reminder duplicated after restart"
        app.click("#bak")
        app.click(".bak-row [data-edit]")
        new_due = due_after(1 if datetime.now().second < 50 else 2)
        set_time(app, new_due)
        expect_popup(app, new_due, 2)
        print("PASS: hidden reminder after restart, persisted deduplication, edited time fires again.", flush=True)
    finally:
        app.close(verify=True)


if __name__ == "__main__":
    if find(None, "DeskCal 1.0.6"):
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
                        *["/r:" + str(framework / (name + ".dll")) for name in ("System.Xaml", "System.Windows.Forms")],
                        *["/r:" + str(app_dir / ("Microsoft.Web.WebView2." + name + ".dll"))
                          for name in ("Core", "Wpf")], str(tests / "InputObserver.cs")], check=True)
        shutil.copyfile(str(app_dir / source.name) + ".config", str(observer) + ".config")
        run(str(app_dir / source.name), str(profile))
        test_reminders(str(app_dir / source.name), str(profile))
