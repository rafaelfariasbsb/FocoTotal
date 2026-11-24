"""Chamadas ao Windows via ctypes e winreg (só biblioteca padrão, sem DLLs extras)."""

from __future__ import annotations

import ctypes
import os
import subprocess
import sys

IS_WINDOWS = sys.platform == "win32"
NO_WINDOW = 0x08000000  # CREATE_NO_WINDOW

if IS_WINDOWS:
    import winreg
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    TH32CS_SNAPPROCESS = 0x00000002
    PROCESS_TERMINATE = 0x0001
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

    class PROCESSENTRY32W(ctypes.Structure):
        _fields_ = [
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ProcessID", wintypes.DWORD),
            ("th32DefaultHeapID", ctypes.c_size_t),
            ("th32ModuleID", wintypes.DWORD),
            ("cntThreads", wintypes.DWORD),
            ("th32ParentProcessID", wintypes.DWORD),
            ("pcPriClassBase", ctypes.c_long),
            ("dwFlags", wintypes.DWORD),
            ("szExeFile", ctypes.c_wchar * 260),
        ]

    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR,
                                                    ctypes.POINTER(wintypes.DWORD)]


def is_admin() -> bool:
    if not IS_WINDOWS:
        return os.geteuid() == 0
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def flush_dns() -> None:
    if IS_WINDOWS:
        try:
            ctypes.windll.dnsapi.DnsFlushResolverCache()
        except Exception:
            pass


def list_processes() -> list[tuple[int, int, str]]:
    """[(pid, pid_do_pai, nome.exe)] de todos os processos."""
    if not IS_WINDOWS:
        return []
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if not snap or snap == INVALID_HANDLE_VALUE:
        return []
    out = []
    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        ok = kernel32.Process32FirstW(snap, ctypes.byref(entry))
        while ok:
            out.append((entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile))
            ok = kernel32.Process32NextW(snap, ctypes.byref(entry))
    finally:
        kernel32.CloseHandle(snap)
    return out


def process_path(pid: int) -> str | None:
    if not IS_WINDOWS:
        return None
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return None
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wintypes.DWORD(len(buf))
        return buf.value if kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)) else None
    finally:
        kernel32.CloseHandle(h)


def kill(pid: int) -> bool:
    if not IS_WINDOWS:
        return False
    h = kernel32.OpenProcess(PROCESS_TERMINATE, False, pid)
    if not h:
        return False
    try:
        return bool(kernel32.TerminateProcess(h, 1))
    finally:
        kernel32.CloseHandle(h)


def run(cmd: list[str]) -> subprocess.CompletedProcess:
    """Roda uma ferramenta do Windows (schtasks, icacls) sem abrir janela."""
    return subprocess.run(cmd, capture_output=True, creationflags=NO_WINDOW if IS_WINDOWS else 0)


# ---------------------------------------------------------------- registro (HKLM)


def reg_get(path: str, name: str):
    if not IS_WINDOWS:
        return None
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path, 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as k:
            return winreg.QueryValueEx(k, name)[0]
    except OSError:
        return None


def reg_set(path: str, name: str, value) -> None:
    if not IS_WINDOWS:
        return
    kind = winreg.REG_DWORD if isinstance(value, int) else (
        winreg.REG_MULTI_SZ if isinstance(value, list) else winreg.REG_SZ)
    with winreg.CreateKeyEx(winreg.HKEY_LOCAL_MACHINE, path, 0, winreg.KEY_WRITE | winreg.KEY_WOW64_64KEY) as k:
        winreg.SetValueEx(k, name, 0, kind, value)


def reg_delete(path: str, name: str) -> None:
    if not IS_WINDOWS:
        return
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path, 0, winreg.KEY_WRITE | winreg.KEY_WOW64_64KEY) as k:
            winreg.DeleteValue(k, name)
    except OSError:
        pass
