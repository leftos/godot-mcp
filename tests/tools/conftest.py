"""Prepares shared state before pytest's xdist workers start: gdtoolkit's cache folder. Holds what the install tests share:
the shells they run the installers under and the stand-in server exe, published once per session."""

from __future__ import annotations

import importlib.metadata
import importlib.util
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import IO

import pytest

if sys.platform == "win32":
    import msvcrt

STAND_IN_PROJECT = Path(__file__).resolve().parent / "fixtures" / "stand-in" / "StandIn.csproj"
# A hang guard only: a passing publish never comes near it.
PUBLISH_HANG_GUARD_SECONDS = 600

# The release installer and the layout module run under PowerShell 7 and Windows PowerShell 5.1.
SHELLS = [
    "pwsh",
    pytest.param(
        "powershell",
        marks=pytest.mark.skipif(shutil.which("powershell") is None, reason="Windows PowerShell 5.1 is not installed"),
    ),
]


def make_gdtoolkit_cache_folder() -> Path | None:
    """Create the folder gdtoolkit's parser writes its pickled grammars into, if gdtoolkit is installed."""
    if importlib.util.find_spec("gdtoolkit") is None:
        return None
    from gdtoolkit.parser.parser import get_cache_directory

    folder = Path(get_cache_directory(), "gdtoolkit", importlib.metadata.version("gdtoolkit"))
    folder.mkdir(parents=True, exist_ok=True)
    return folder


def pytest_configure(config: pytest.Config) -> None:
    """Make the folder in the controller, before the xdist workers race to create it."""
    make_gdtoolkit_cache_folder()


@pytest.fixture(scope="session")
def stand_in(tmp_path_factory: pytest.TempPathFactory, worker_id: str) -> Path:
    """The install tests' stand-in godot-mcp.exe (tests/tools/fixtures/stand-in), published into a folder every xdist
    worker of this session shares, by the first worker to get there: --list-tools exits 0, --sweep-agents exits with
    STANDIN_SWEEP_STATUS, --start <exe> <flag> runs <exe> <flag> as a launcher would, and <flag> alone runs while that
    file exists."""
    shared = tmp_path_factory.getbasetemp() if worker_id == "master" else tmp_path_factory.getbasetemp().parent
    exe = shared / "stand-in" / "godot-mcp.exe"
    with open(shared / "stand-in.lock", "a+b") as lock:
        _lock(lock)
        try:
            if not exe.is_file():
                _publish_stand_in(exe.parent)
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


def _publish_stand_in(folder: Path) -> None:
    command = ["dotnet", "publish", str(STAND_IN_PROJECT), "-c", "Release", "-o", str(folder)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=PUBLISH_HANG_GUARD_SECONDS, check=False)
    if result.returncode != 0:
        pytest.fail(f"publishing the stand-in failed (status {result.returncode}):\n{result.stdout[-3000:]}{result.stderr[-3000:]}")
