"""Tests for tools/linelength.py: the 150-character limit, line endings, and which files are reported."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from types import ModuleType

import pytest

TOOL_PATH = Path(__file__).resolve().parents[2] / "tools" / "linelength.py"


def _load_tool() -> ModuleType:
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("linelength", TOOL_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


ll = _load_tool()


def _write(path: Path, lines: list[str], ending: str = "\n") -> Path:
    path.write_bytes(ending.join(lines).encode("utf-8") + ending.encode("utf-8"))
    return path


def test_a_150_character_line_passes(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    source = _write(tmp_path / "Ok.cs", ["x" * 150])
    assert ll.main([str(source)]) == 0
    assert capsys.readouterr().out == ""


def test_a_151_character_line_fails_with_its_path_and_line(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    source = _write(tmp_path / "Long.cs", ["short", "y" * 151, "short"])
    assert ll.main([str(source)]) == 1
    assert f"{source}:2: 151 characters (limit 150)" in capsys.readouterr().out


def test_crlf_endings_are_not_counted(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    source = _write(tmp_path / "Crlf.cs", ["z" * 150, "z" * 150], ending="\r\n")
    assert ll.main([str(source)]) == 0
    assert capsys.readouterr().out == ""


def test_a_file_with_no_long_lines_exits_0(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    source = _write(tmp_path / "Short.cs", ["namespace A;", "", "internal sealed class B { }"])
    assert ll.main([str(source)]) == 0
    assert capsys.readouterr().out == ""


def test_several_files_report_only_the_bad_one(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    good = _write(tmp_path / "Good.cs", ["a" * 150])
    bad = _write(tmp_path / "Bad.cs", ["b" * 10, "b" * 10, "b" * 160])
    also_good = _write(tmp_path / "AlsoGood.cs", ["c" * 20])
    assert ll.main([str(good), str(bad), str(also_good)]) == 1
    out = capsys.readouterr().out
    assert out.splitlines() == [f"{bad}:3: 160 characters (limit 150)"]
