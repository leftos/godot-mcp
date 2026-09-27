"""A stand-in for a running godot-mcp server, for the install tests: ping.exe copied into an install folder as
godot-mcp.exe, started by a pwsh parent whose command line carries a Claude Code --session-id, or none."""

from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path

SESSION_ID = "11111111-2222-3333-4444-555555555555"
PROJECT = r"C:\temp\proj"
PING = Path(os.environ.get("SYSTEMROOT", r"C:\Windows")) / "System32" / "PING.EXE"


@dataclass(frozen=True)
class FakeServer:
    parent: subprocess.Popen[bytes]
    pid: int

    def is_running(self) -> bool:
        listing = subprocess.run(["tasklist", "/FI", f"PID eq {self.pid}", "/NH"], capture_output=True, text=True, timeout=30, check=True)
        return "godot-mcp.exe" in listing.stdout

    def stop(self) -> None:
        if self.is_running():
            os.kill(self.pid, 9)
        self.parent.kill()
        self.parent.wait(timeout=30)


def _child_pid(parent: int) -> int:
    """The pid of the godot-mcp.exe the parent started, waited for up to 20 s."""
    source = (
        "$deadline = (Get-Date).AddSeconds(20); "
        "do { $child = Get-CimInstance -ClassName Win32_Process "
        f"-Filter \"ParentProcessId = {parent} AND Name = 'godot-mcp.exe'\"; "
        "if ($child) { $child.ProcessId; exit 0 }; Start-Sleep -Milliseconds 200 } "
        "while ((Get-Date) -lt $deadline); exit 1"
    )
    result = subprocess.run(["pwsh", "-NoProfile", "-Command", source], capture_output=True, text=True, timeout=60, check=True)
    return int(result.stdout.strip())


def start_fake_server(install_dir: Path, *, session_id: str | None) -> FakeServer:
    """Copies ping.exe to <install_dir>/godot-mcp.exe and starts it for 60 s from a pwsh parent whose command line ends
    in a comment carrying --session-id <session_id>, or no comment when session_id is None."""
    install_dir.mkdir(parents=True, exist_ok=True)
    exe = install_dir / "godot-mcp.exe"
    shutil.copyfile(PING, exe)
    command = f"& '{exe}' -n 60 127.0.0.1 | Out-Null"
    if session_id is not None:
        command += f"; # --session-id {session_id}"
    parent = subprocess.Popen(
        ["pwsh", "-NoProfile", "-Command", command], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
    )
    try:
        return FakeServer(parent=parent, pid=_child_pid(parent.pid))
    except Exception:
        parent.kill()
        raise


def write_transcript(home: Path, session_id: str) -> None:
    """A Claude Code transcript under <home>/.claude/projects, whose second line is the first to carry the project as cwd."""
    folder = home / ".claude" / "projects" / "C--temp-proj"
    folder.mkdir(parents=True)
    escaped = PROJECT.replace("\\", "\\\\")
    lines = ['{"type":"summary","summary":"a session"}', f'{{"type":"user","cwd":"{escaped}","sessionId":"{session_id}"}}']
    (folder / f"{session_id}.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8")
