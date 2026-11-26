"""Run the GUI test on an isolated, visible Win32 desktop of the hosted VM."""
import ctypes as C
from ctypes import wintypes as W
import msvcrt
import os
import subprocess
import sys
import tempfile

class Start(C.Structure):
    _fields_ = [("cb", W.DWORD), ("reserved", W.LPWSTR), ("desktop", W.LPWSTR),
                ("title", W.LPWSTR)] + [(n, W.DWORD) for n in
                ("x", "y", "width", "height", "chars_x", "chars_y", "fill", "flags")] + [
                ("show", W.WORD), ("reserved_size", W.WORD), ("reserved2", C.c_void_p),
                ("stdin", W.HANDLE), ("stdout", W.HANDLE), ("stderr", W.HANDLE)]

class Process(C.Structure):
    _fields_ = [("process", W.HANDLE), ("thread", W.HANDLE), ("pid", W.DWORD), ("tid", W.DWORD)]

def bind(module, name, result, *args):
    fn = getattr(C.WinDLL(module, use_last_error=True), name)
    fn.argtypes, fn.restype = args, result
    return fn

open_input = bind("user32", "OpenInputDesktop", W.HANDLE, W.DWORD, W.BOOL, W.DWORD)
create = bind("user32", "CreateDesktopW", W.HANDLE, W.LPCWSTR, W.LPCWSTR, C.c_void_p,
              W.DWORD, W.DWORD, C.c_void_p)
switch = bind("user32", "SwitchDesktop", W.BOOL, W.HANDLE)
close = bind("user32", "CloseDesktop", W.BOOL, W.HANDLE)
close_handle = bind("kernel32", "CloseHandle", W.BOOL, W.HANDLE)
launch = bind("kernel32", "CreateProcessW", W.BOOL, W.LPCWSTR, W.LPWSTR, C.c_void_p,
              C.c_void_p, W.BOOL, W.DWORD, C.c_void_p, W.LPCWSTR, C.POINTER(Start), C.POINTER(Process))
poll = bind("kernel32", "WaitForSingleObject", W.DWORD, W.HANDLE, W.DWORD)
status = bind("kernel32", "GetExitCodeProcess", W.BOOL, W.HANDLE, C.POINTER(W.DWORD))

def checked(result):
    if not result:
        raise C.WinError(C.get_last_error())
    return result

def main():
    name = "DeskCalTest" + str(os.getpid())
    original = checked(open_input(0, False, 0x100))
    try:
        desk = checked(create(name, None, None, 0, 0x1ff, None))
        try:
            checked(switch(desk))
            info, proc = Start(), Process()
            info.cb, info.desktop = C.sizeof(info), "WinSta0\\" + name
            cmd = C.create_unicode_buffer(subprocess.list2cmdline([sys.executable, *sys.argv[1:]]))
            with tempfile.TemporaryFile() as log, open(os.devnull, 'rb') as devnull:
                info.flags = 0x100  # STARTF_USESTDHANDLES
                info.stdout = info.stderr = msvcrt.get_osfhandle(log.fileno())
                info.stdin = msvcrt.get_osfhandle(devnull.fileno())
                for handle in (info.stdout, info.stdin):
                    os.set_handle_inheritable(handle, True)
                checked(launch(sys.executable, cmd, None, None, True, 0x08000000,
                               None, None, C.byref(info), C.byref(proc)))
                try:
                    if poll(proc.process, 600000) != 0:
                        raise TimeoutError("isolated Windows desktop test")
                    code = W.DWORD()
                    checked(status(proc.process, C.byref(code)))
                    return code.value
                finally:
                    close_handle(proc.thread)
                    close_handle(proc.process)
                    log.seek(0)
                    print(log.read().decode(errors="replace"), flush=True)
        finally:
            switch(original)
            close(desk)
    finally:
        close(original)

if __name__ == "__main__":
    sys.exit(main())
