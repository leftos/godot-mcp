"""Tests for tools/drive.py, run against tests/tools/fake_mcp_server.py: a stdio server answering from canned replies."""

from __future__ import annotations

import base64
import importlib.util
import json
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from types import ModuleType
from typing import Any

import pytest

TOOL_PATH = Path(__file__).resolve().parents[2] / "tools" / "drive.py"
FAKE_SERVER = Path(__file__).resolve().with_name("fake_mcp_server.py")
GATE_PATH = Path(__file__).resolve().parents[2] / "tools" / "gate.ps1"


def _load_tool() -> ModuleType:
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("drive", TOOL_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


drive = _load_tool()


@dataclass(frozen=True)
class Fake:
    script: Path
    record: Path

    def entries(self) -> list[dict[str, Any]]:
        lines = self.record.read_text(encoding="utf-8").splitlines()
        return [json.loads(line) for line in lines]

    def messages(self) -> list[dict[str, Any]]:
        return [entry for entry in self.entries() if "method" in entry]

    def events(self) -> list[str]:
        return [entry["event"] for entry in self.entries() if "event" in entry]

    def tool_calls(self) -> list[dict[str, Any]]:
        return [message["params"] for message in self.messages() if message["method"] == "tools/call"]


def _fake(tmp_path: Path, replies: list[dict[str, Any]], *, hang_on_eof: bool = False) -> Fake:
    fake = Fake(script=tmp_path / "script.json", record=tmp_path / "record.jsonl")
    fake.script.write_text(json.dumps({"replies": replies, "hang_on_eof": hang_on_eof}), encoding="utf-8")
    return fake


def _fake_command(fake: Fake) -> list[str]:
    return [sys.executable, str(FAKE_SERVER), str(fake.script), str(fake.record)]


def _drive_arguments(tmp_path: Path, calls: str, server: list[str]) -> list[str]:
    calls_path = tmp_path / "calls.json"
    calls_path.write_text(calls, encoding="utf-8")
    return ["--calls", str(calls_path), "--images", str(tmp_path / "images"), "--server-log", str(tmp_path / "server.log"), *server]


def _drive(tmp_path: Path, fake: Fake, calls: str) -> int:
    return drive.main(_drive_arguments(tmp_path, calls, _fake_command(fake)))


def _text(text: str, *, is_error: bool = False) -> dict[str, Any]:
    return {"result": {"content": [{"type": "text", "text": text}], "isError": is_error}}


def test_calls_run_in_order_and_print_their_text(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    fake = _fake(tmp_path, [_text('{"sessions":[]}'), _text("line one\nline two")])
    calls = json.dumps([{"tool": "list_sessions"}, {"tool": "get_debug_output", "arguments": {"limit": 5}}])

    status = _drive(tmp_path, fake, calls)

    assert status == 0
    assert capsys.readouterr().out == '== 1 list_sessions\n{"sessions":[]}\n== 2 get_debug_output\nline one\nline two\n'
    methods = [message["method"] for message in fake.messages()]
    assert methods == ["initialize", "notifications/initialized", "tools/call", "tools/call"]
    assert fake.messages()[0]["params"]["protocolVersion"] == "2025-11-25"
    assert fake.tool_calls() == [{"name": "list_sessions", "arguments": {}}, {"name": "get_debug_output", "arguments": {"limit": 5}}]
    assert fake.events() == ["start", "eof"]


def test_an_image_is_saved_and_its_path_printed(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    png = b"\x89PNG\r\n\x1a\nnot really a picture"
    image = {"type": "image", "data": base64.b64encode(png).decode("ascii"), "mimeType": "image/png"}
    fake = _fake(tmp_path, [{"result": {"content": [{"type": "text", "text": "captured"}, image]}}])

    status = _drive(tmp_path, fake, '[{"tool": "take_screenshot"}]')

    saved = tmp_path / "images" / "1-2.png"
    assert status == 0
    assert saved.read_bytes() == png
    assert capsys.readouterr().out == f"== 1 take_screenshot\ncaptured\n[image {saved}]\n"


def test_an_error_result_stops_the_run(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    fake = _fake(tmp_path, [_text("ok"), _text("No session named 'nope'.", is_error=True), _text("never")])
    calls = json.dumps([{"tool": "list_sessions"}, {"tool": "get_debug_output", "arguments": {"session": "nope"}}, {"tool": "list_sessions"}])

    status = _drive(tmp_path, fake, calls)

    assert status == 1
    out = capsys.readouterr().out
    assert out.startswith("== 1 list_sessions\nok\n== 2 get_debug_output\nNo session named 'nope'.\n")
    assert "drive: call 2 (get_debug_output) failed; 1 later call not run" in out
    assert out.endswith(f"server log: {tmp_path / 'server.log'}\n")
    assert "never" not in out
    assert [call["name"] for call in fake.tool_calls()] == ["list_sessions", "get_debug_output"]
    assert fake.events() == ["start", "eof"]


def test_a_protocol_error_stops_the_run(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    fake = _fake(tmp_path, [{"error": {"code": -32602, "message": "Unknown tool: 'no_such_tool'"}}, _text("never")])

    status = _drive(tmp_path, fake, '[{"tool": "no_such_tool"}, {"tool": "list_sessions"}]')

    assert status == 1
    out = capsys.readouterr().out
    assert "== 1 no_such_tool\nerror -32602: Unknown tool: 'no_such_tool'\n" in out
    assert "drive: call 1 (no_such_tool) failed; 1 later call not run" in out
    assert out.endswith(f"server log: {tmp_path / 'server.log'}\n")
    assert len(fake.tool_calls()) == 1
    assert fake.events() == ["start", "eof"]


@pytest.mark.parametrize(
    ("calls", "message"),
    [
        ('{"tool": "list_sessions"}', "must be a JSON array of calls, not an object"),
        ("[]", "holds no calls"),
        ("[list_sessions]", "is not JSON"),
        ('[{"tool": "list_sessions"}, "get_errors"]', 'call 2 (index 1) must be an object {"tool": ..., "arguments": {...}}, not a string'),
        ('[{"arguments": {}}]', 'call 1 (index 0) has no "tool" name'),
        ('[{"tool": ""}]', 'call 1 (index 0) has no "tool" name'),
        ('[{"tool": "click", "arguments": []}]', 'call 1 (index 0) has "arguments" that are an array, not an object'),
        ('[{"tool": "click", "args": {}}]', 'call 1 (index 0) has unknown keys: "args"'),
    ],
)
def test_a_malformed_calls_file_fails_before_any_server_starts(tmp_path: Path, capsys: pytest.CaptureFixture[str], calls: str, message: str) -> None:
    fake = _fake(tmp_path, [])

    status = _drive(tmp_path, fake, calls)

    assert status == 2
    assert message in capsys.readouterr().err
    assert not fake.record.exists()


def test_a_server_that_outlives_its_stdin_is_killed(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(drive, "STOP_SECONDS", 0.5)
    fake = _fake(tmp_path, [_text("ok")], hang_on_eof=True)

    status = _drive(tmp_path, fake, '[{"tool": "list_sessions"}]')

    assert status == 0
    assert fake.events()[:2] == ["start", "eof"]
    entries = len(fake.entries())
    time.sleep(0.3)
    assert len(fake.entries()) == entries


def test_the_server_log_goes_to_its_own_file(tmp_path: Path, capfd: pytest.CaptureFixture[str]) -> None:
    fake = _fake(tmp_path, [_text("ok")])

    status = _drive(tmp_path, fake, '[{"tool": "list_sessions"}]')

    assert status == 0
    captured = capfd.readouterr()
    assert captured.out == "== 1 list_sessions\nok\n"
    assert "fake server" not in captured.err
    assert "fake server: answering list_sessions" in (tmp_path / "server.log").read_text(encoding="utf-8")


def test_a_server_that_dies_points_at_its_log(tmp_path: Path, capfd: pytest.CaptureFixture[str]) -> None:
    dying = [sys.executable, "-c", "import sys; print('dying before initialize', file=sys.stderr)"]

    status = drive.main(_drive_arguments(tmp_path, '[{"tool": "list_sessions"}]', dying))

    assert status == 1
    out = capfd.readouterr().out
    assert "drive: the server " in out
    assert out.endswith(f"server log: {tmp_path / 'server.log'}\n")
    assert "dying before initialize" in (tmp_path / "server.log").read_text(encoding="utf-8")


@pytest.mark.skipif(shutil.which("pwsh") is None, reason="tools/gate.ps1 needs PowerShell 7")
@pytest.mark.parametrize(("gate_options", "expected_status"), [(["-NoMarkers"], 0), ([], 1)])
def test_the_gate_takes_a_drive_by_its_exit_status_alone(tmp_path: Path, gate_options: list[str], expected_status: int) -> None:
    quoted_failure = "E 0:00:01:250 main.gd:12 @ _ready(): error while loading\n  failed: 1"
    fake = _fake(tmp_path, [_text(quoted_failure)])
    command = [sys.executable, str(TOOL_PATH), *_drive_arguments(tmp_path, '[{"tool": "get_errors"}]', _fake_command(fake))]
    log = tmp_path / "drive.log"
    gate = ["pwsh", "-NoProfile", "-File", str(GATE_PATH), *gate_options, "-Log", str(log), "-TimeoutSeconds", "60", "--", *command]

    result = subprocess.run(gate, capture_output=True, text=True, timeout=90, check=False)

    assert result.returncode == expected_status, result.stdout + result.stderr
    assert "  failed: 1" in log.read_text(encoding="utf-8")
