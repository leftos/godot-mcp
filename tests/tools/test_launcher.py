"""Tests for the launcher, published once per pytest session with NativeAOT, run in a temporary install layout whose
server is a copy of cmd.exe: it echoes, exits with a chosen code and starts a grandchild (ping.exe) on request."""

from __future__ import annotations

import ctypes
import os
import shutil
import subprocess
import sys
import time
from collections.abc import Callable
from pathlib import Path
from typing import IO

import pytest

if sys.platform == "win32":
    import msvcrt

REPO = Path(__file__).resolve().parents[2]
LAUNCHER_PROJECT = REPO / "src" / "GodotMcp.Launcher" / "GodotMcp.Launcher.csproj"
CMD = Path(os.environ.get("SYSTEMROOT", r"C:\Windows")) / "System32" / "cmd.exe"
# Hang guards only: a passing run never comes near them.
HANG_GUARD_SECONDS = 120
PUBLISH_HANG_GUARD_SECONDS = 600
SYNCHRONIZE = 0x00100000
PROCESS_TERMINATE = 0x0001
WAIT_OBJECT_0 = 0

pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="the launcher is a Windows executable")


@pytest.fixture(scope="session")
def published_launcher(tmp_path_factory: pytest.TempPathFactory, worker_id: str) -> Path:
    """The launcher published into a folder every xdist worker of this session shares, by the first worker to get there."""
    shared = tmp_path_factory.getbasetemp() if worker_id == "master" else tmp_path_factory.getbasetemp().parent
    exe = shared / "launcher" / "godot-mcp.exe"
    with open(shared / "launcher.lock", "a+b") as lock:
        _lock(lock)
        try:
            if not exe.is_file():
                _publish(exe.parent)
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
    return exe


def _lock(lock: IO[bytes]) -> None:
    deadline = time.monotonic() + PUBLISH_HANG_GUARD_SECONDS
    while True:
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            return
        except OSError:
            if time.monotonic() > deadline:
                pytest.fail(f"hung: another worker held {lock.name} for {PUBLISH_HANG_GUARD_SECONDS} s")
            time.sleep(0.1)


def _publish(folder: Path) -> None:
    command = ["dotnet", "publish", str(LAUNCHER_PROJECT), "-c", "Release", "-r", "win-x64", "-o", str(folder)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=PUBLISH_HANG_GUARD_SECONDS, check=False)
    if result.returncode != 0:
        pytest.fail(f"publishing the launcher failed (status {result.returncode}):\n{result.stdout[-3000:]}{result.stderr[-3000:]}")


def _layout(tmp_path: Path, published: Path, pointer: str) -> Path:
    """The launcher at <tmp>/godot-mcp.exe, the pointer at <tmp>/current, and cmd.exe as <tmp>/versions/A/godot-mcp.exe."""
    launcher = tmp_path / "godot-mcp.exe"
    shutil.copyfile(published, launcher)
    (tmp_path / "current").write_text(pointer, encoding="utf-8")
    server = tmp_path / "versions" / "A" / "godot-mcp.exe"
    server.parent.mkdir(parents=True)
    shutil.copyfile(CMD, server)
    return launcher


def _run(launcher: Path, *arguments: str) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run([str(launcher), *arguments], stdin=subprocess.DEVNULL, capture_output=True, timeout=HANG_GUARD_SECONDS, check=False)


def test_stdout_carries_only_the_servers_bytes(tmp_path: Path, published_launcher: Path) -> None:
    launcher = _layout(tmp_path, published_launcher, "A\n")

    result = _run(launcher, "/c", "echo", "%GODOT_MCP_LAUNCHER%")

    assert result.returncode == 0, result.stderr
    assert result.stderr == b""
    assert result.stdout.endswith(b"\r\n")
    lines = result.stdout.decode().splitlines()
    assert len(lines) == 1
    assert os.path.samefile(lines[0], launcher)


def test_the_servers_exit_code_passes_through(tmp_path: Path, published_launcher: Path) -> None:
    launcher = _layout(tmp_path, published_launcher, "A")

    result = _run(launcher, "/c", "exit", "7")

    assert result.returncode == 7, result.stderr
    assert result.stdout == b""


@pytest.mark.parametrize(
    ("pointer", "message"),
    [
        ("", "names no version folder: its first line is blank."),
        ("..\n", "names '..', which is not a single folder name."),
        ("A\\B\n", "names 'A\\B', which is not a single folder name."),
        ("B\n", "names version 'B', but "),
    ],
)
def test_a_bad_pointer_is_one_stderr_line_and_exit_1(tmp_path: Path, published_launcher: Path, pointer: str, message: str) -> None:
    launcher = _layout(tmp_path, published_launcher, pointer)

    result = _run(launcher, "/c", "echo", "started")

    assert result.returncode == 1
    assert result.stdout == b""
    lines = result.stderr.decode().splitlines()
    assert len(lines) == 1, lines
    assert lines[0].startswith(f"godot-mcp launcher: {tmp_path / 'current'} {message}")


def test_killing_the_launcher_ends_the_server_but_not_its_child(tmp_path: Path, published_launcher: Path) -> None:
    launcher = _layout(tmp_path, published_launcher, "A")
    output = tmp_path / "ping.txt"
    process = subprocess.Popen(
        [str(launcher), "/c", "ping", "-n", "120", "127.0.0.1", ">", str(output)],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    server = grandchild = 0
    try:
        # A listed child is not yet a running one: the grandchild's first output line says both are running.
        _wait_until(lambda: _line_count(output) >= 1, "the grandchild's first output line")
        server_pid, grandchild_pid = _server_and_grandchild(process.pid)
        server = _open(server_pid)
        grandchild = _open(grandchild_pid)

        process.kill()
        process.wait(timeout=HANG_GUARD_SECONDS)

        assert _kernel32().WaitForSingleObject(server, HANG_GUARD_SECONDS * 1000) == WAIT_OBJECT_0, "the server outlived its launcher"
        lines_at_kill = _line_count(output)
        _wait_until(lambda: _line_count(output) > lines_at_kill, "the grandchild's next output line after the kill")
    finally:
        _cleanup(process, server, grandchild)


def _kernel32() -> ctypes.WinDLL:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
    kernel32.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel32.WaitForSingleObject.restype = ctypes.c_uint32
    kernel32.TerminateProcess.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    return kernel32


def _open(pid: int) -> int:
    handle = _kernel32().OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE, 0, pid)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    return handle


def _cleanup(process: subprocess.Popen[bytes], *handles: int) -> None:
    kernel32 = _kernel32()
    if process.poll() is None:
        process.kill()
        process.wait(timeout=HANG_GUARD_SECONDS)
    for handle in handles:
        if handle:
            kernel32.TerminateProcess(handle, 1)
            kernel32.CloseHandle(handle)


def _server_and_grandchild(launcher_pid: int) -> tuple[int, int]:
    """The pids of the server the launcher started and of the ping.exe the server started."""
    source = (
        f"$server = Get-CimInstance -ClassName Win32_Process -Filter 'ParentProcessId = {launcher_pid}'; "
        "$ping = Get-CimInstance -ClassName Win32_Process -Filter \"ParentProcessId = $($server.ProcessId) AND Name = 'PING.EXE'\"; "
        '"$($server.ProcessId) $($ping.ProcessId)"'
    )
    result = subprocess.run(["pwsh", "-NoProfile", "-Command", source], capture_output=True, text=True, timeout=HANG_GUARD_SECONDS, check=True)
    server_pid, grandchild_pid = result.stdout.split()
    return int(server_pid), int(grandchild_pid)


def _line_count(path: Path) -> int:
    try:
        return len(path.read_bytes().splitlines())
    except FileNotFoundError:
        return 0


def _wait_until(condition: Callable[[], bool], what: str) -> None:
    """Polls a file the processes under test write; the deadline is a hang guard, not a time the test asserts."""
    deadline = time.monotonic() + HANG_GUARD_SECONDS
    while not condition():
        if time.monotonic() > deadline:
            pytest.fail(f"hung: no sign of {what} within {HANG_GUARD_SECONDS} s")
        time.sleep(0.05)
