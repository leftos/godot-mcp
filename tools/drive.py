"""Drives an MCP server over stdio with a file of tool calls, in order, and prints each result.

Usage: python tools/drive.py --calls <file.json> --images <dir> --server-log <file> <server> [server args...]

The calls file is a JSON array of `{"tool": "<name>", "arguments": {...}}` objects (`arguments` defaults to `{}`), read
and checked before the server starts. Each call prints `== <n> <tool>`, then its content: text as it is, an image saved
to `<images>/<n>-<i><ext>` and printed as `[image <path>]`, anything else as its JSON. The server's stderr goes to the
server log, never into this output. The first call whose result has `isError: true`, or that the server answers with a
protocol error, is printed and stops the run; that, or a server that stops answering, ends the output with
`server log: <path>`. The server is always stopped at the end: its stdin is closed, and it is killed when it has not
exited after STOP_SECONDS.

Exit status: 0 when every call succeeded, 1 when a call failed or the server did not answer, 2 for a malformed calls
file or command line, 130 when interrupted.
"""

from __future__ import annotations

import argparse
import base64
import contextlib
import json
import mimetypes
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from types import TracebackType
from typing import Any

PROTOCOL_VERSION = "2025-11-25"
STOP_SECONDS = 5.0
CALL_KEYS = frozenset({"tool", "arguments"})
JSON_TYPES = {dict: "an object", list: "an array", str: "a string", bool: "a boolean", int: "a number", float: "a number"}


class CallsError(Exception):
    """The calls file cannot be read or does not hold a valid list of calls."""


class DriveError(Exception):
    """The server could not be started, or stopped answering."""


@dataclass(frozen=True)
class Call:
    tool: str
    arguments: dict[str, Any]


def _json_type(value: object) -> str:
    return "null" if value is None else JSON_TYPES.get(type(value), type(value).__name__)


def _parse_call(index: int, entry: object) -> Call:
    where = f"call {index + 1} (index {index})"
    if not isinstance(entry, dict):
        raise CallsError(f'{where} must be an object {{"tool": ..., "arguments": {{...}}}}, not {_json_type(entry)}')
    unknown = sorted(set(entry) - CALL_KEYS)
    if unknown:
        raise CallsError(f"{where} has unknown keys: {', '.join(json.dumps(key) for key in unknown)}")
    tool = entry.get("tool")
    if not isinstance(tool, str) or not tool:
        raise CallsError(f'{where} has no "tool" name')
    arguments = entry.get("arguments", {})
    if not isinstance(arguments, dict):
        raise CallsError(f'{where} has "arguments" that are {_json_type(arguments)}, not an object')
    return Call(tool, arguments)


def load_calls(path: Path) -> list[Call]:
    """Reads and checks a calls file.

    Args:
        path: The JSON file holding an array of calls.

    Returns:
        The calls, in the file's order.

    Raises:
        CallsError: The file cannot be read, is not JSON, or an entry is not a valid call; the message names the entry.
    """
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except OSError as error:
        raise CallsError(f"{path} cannot be read: {error.strerror}") from error
    except json.JSONDecodeError as error:
        raise CallsError(f"{path} is not JSON: {error}") from error
    if not isinstance(data, list):
        raise CallsError(f"{path} must be a JSON array of calls, not {_json_type(data)}")
    if not data:
        raise CallsError(f"{path} holds no calls")
    try:
        return [_parse_call(index, entry) for index, entry in enumerate(data)]
    except CallsError as error:
        raise CallsError(f"{path}: {error}") from error


class Server:
    """An MCP server process spoken to over its stdin and stdout, one JSON-RPC message per line."""

    def __init__(self, command: list[str], log: Path) -> None:
        log.parent.mkdir(parents=True, exist_ok=True)
        self._log = log.open("wb")
        try:
            self._process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self._log)
        except OSError as error:
            self._log.close()
            raise DriveError(f"cannot start the server {command[0]}: {error.strerror}") from error
        self._last_id = 0

    def __enter__(self) -> Server:
        return self

    def __exit__(self, kind: type[BaseException] | None, error: BaseException | None, trace: TracebackType | None) -> None:
        self.stop()

    def request(self, method: str, params: dict[str, Any]) -> dict[str, Any]:
        """Sends a request and waits for the response with its id, skipping every other message."""
        self._last_id += 1
        self._send({"jsonrpc": "2.0", "id": self._last_id, "method": method, "params": params})
        while True:
            message = self._read(method)
            if message.get("id") == self._last_id and "method" not in message:
                return message

    def notify(self, method: str) -> None:
        """Sends a notification, which has no response."""
        self._send({"jsonrpc": "2.0", "method": method})

    def stop(self) -> None:
        """Closes the server's stdin and waits for it to exit, killing it after STOP_SECONDS."""
        process = self._process
        # A close fails only when the server has already exited and closed its end; the wait below reaps it.
        with contextlib.suppress(OSError):
            process.stdin.close()
        try:
            process.wait(timeout=STOP_SECONDS)
        except subprocess.TimeoutExpired:
            print(f"drive: the server did not exit {STOP_SECONDS} s after its input closed; killing it", file=sys.stderr)
            process.kill()
            process.wait()
        process.stdout.close()
        self._log.close()

    def _send(self, message: dict[str, Any]) -> None:
        try:
            self._process.stdin.write(json.dumps(message).encode("utf-8") + b"\n")
            self._process.stdin.flush()
        except OSError as error:
            raise DriveError(f"the server stopped reading its input (exit status {self._process.poll()})") from error

    def _read(self, method: str) -> dict[str, Any]:
        while True:
            line = self._process.stdout.readline()
            if not line:
                raise DriveError(f"the server closed its output before answering {method} (exit status {self._process.poll()})")
            if not line.strip():
                continue
            try:
                return json.loads(line)
            except json.JSONDecodeError as error:
                raise DriveError(f"the server wrote a line that is not JSON: {line[:200]!r}") from error


def _initialize(server: Server) -> None:
    params = {"protocolVersion": PROTOCOL_VERSION, "capabilities": {}, "clientInfo": {"name": "godot-mcp-drive", "version": "1.0.0"}}
    response = server.request("initialize", params)
    if "error" in response:
        raise DriveError(f"the server refused initialize: {json.dumps(response['error'])}")
    server.notify("notifications/initialized")


def _save_image(item: dict[str, Any], path_stem: Path) -> Path:
    extension = mimetypes.guess_extension(str(item.get("mimeType", ""))) or ".bin"
    path = path_stem.with_name(path_stem.name + extension)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(base64.b64decode(item.get("data", "")))
    return path


def _print_content(content: list[dict[str, Any]], number: int, images: Path) -> None:
    for index, item in enumerate(content, start=1):
        kind = item.get("type")
        if kind == "text":
            print(item.get("text", ""), flush=True)
        elif kind == "image":
            print(f"[image {_save_image(item, images / f'{number}-{index}')}]", flush=True)
        else:
            print(json.dumps(item), flush=True)


def _call_failed(response: dict[str, Any], number: int, images: Path) -> bool:
    error = response.get("error")
    if error is not None:
        print(f"error {error.get('code')}: {error.get('message')}", flush=True)
        return True
    result = response.get("result", {})
    _print_content(result.get("content", []), number, images)
    return result.get("isError") is True


def run(server: Server, calls: list[Call], images: Path) -> int:
    """Initializes the server, then runs the calls in order, stopping at the first that fails.

    Args:
        server: The started server.
        calls: The calls to run.
        images: The folder images are saved in.

    Returns:
        0 when every call succeeded, else 1.
    """
    _initialize(server)
    for number, call in enumerate(calls, start=1):
        print(f"== {number} {call.tool}", flush=True)
        response = server.request("tools/call", {"name": call.tool, "arguments": call.arguments})
        if _call_failed(response, number, images):
            later = len(calls) - number
            print(f"drive: call {number} ({call.tool}) failed; {later} later call{'' if later == 1 else 's'} not run", flush=True)
            return 1
    return 0


def _parse_arguments(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog="drive.py", description="Drives an MCP server over stdio with a file of tool calls.")
    parser.add_argument("--calls", type=Path, required=True, help="a JSON array of {tool, arguments} objects")
    parser.add_argument("--images", type=Path, required=True, help="the folder images are saved in")
    parser.add_argument("--server-log", type=Path, required=True, help="the file the server's stderr is written to")
    parser.add_argument("server", nargs=argparse.REMAINDER, help="the server's command line")
    arguments = parser.parse_args(argv)
    if not arguments.server:
        parser.error("the server's command line is missing")
    return arguments


def main(argv: list[str] | None = None) -> int:
    """Runs drive.py's command line; returns the exit status."""
    arguments = _parse_arguments(argv)
    try:
        calls = load_calls(arguments.calls)
    except CallsError as error:
        print(f"drive: {error}", file=sys.stderr)
        return 2
    try:
        with Server(arguments.server, arguments.server_log) as server:
            status = run(server, calls, arguments.images)
    except DriveError as error:
        print(f"drive: {error}", flush=True)
        status = 1
    except KeyboardInterrupt:
        print("drive: interrupted; the server was stopped", flush=True)
        return 130
    if status != 0:
        print(f"server log: {arguments.server_log}", flush=True)
    return status


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
