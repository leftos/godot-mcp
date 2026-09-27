"""A stand-in for the godot-mcp server over stdio, for tests/tools/test_drive.py.

It answers `initialize` and each `tools/call` from a script of canned replies, sends a notification and a stderr line
before every tool reply (neither of which a client may take for the reply), and records every message it reads.

Usage: python fake_mcp_server.py <script.json> <record.jsonl>

The script is `{"replies": [{"result": {...}} or {"error": {...}}, ...], "hang_on_eof": false}`: the k-th `tools/call`
gets the k-th reply. The record gets `{"event": "start"}`, then every message read, then `{"event": "eof"}` when stdin
closes; with `hang_on_eof` it then appends `{"event": "alive"}` every 50 ms instead of exiting.
"""

from __future__ import annotations

import json
import sys
import time
from pathlib import Path
from typing import Any, BinaryIO


def _record(path: Path, entry: dict[str, Any]) -> None:
    with path.open("a", encoding="utf-8", newline="\n") as record:
        record.write(json.dumps(entry) + "\n")


def _write(out: BinaryIO, message: dict[str, Any]) -> None:
    out.write(json.dumps(message).encode("utf-8") + b"\n")
    out.flush()


def _answer(out: BinaryIO, message: dict[str, Any], replies: list[dict[str, Any]]) -> None:
    if message["method"] == "initialize":
        result = {"protocolVersion": message["params"]["protocolVersion"], "capabilities": {"tools": {}}, "serverInfo": {"name": "fake"}}
        _write(out, {"jsonrpc": "2.0", "id": message["id"], "result": result})
        return
    reply = replies.pop(0)
    print(f"fake server: answering {message['params']['name']}", file=sys.stderr, flush=True)
    _write(out, {"jsonrpc": "2.0", "method": "notifications/message", "params": {"level": "info", "data": "working"}})
    _write(out, {"jsonrpc": "2.0", "id": message["id"], **reply})


def main() -> None:
    script = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    record = Path(sys.argv[2])
    replies = list(script["replies"])
    _record(record, {"event": "start"})
    for line in sys.stdin.buffer:
        message = json.loads(line)
        _record(record, message)
        if "id" in message:
            _answer(sys.stdout.buffer, message, replies)
    _record(record, {"event": "eof"})
    while script.get("hang_on_eof"):
        _record(record, {"event": "alive"})
        time.sleep(0.05)


if __name__ == "__main__":
    main()
