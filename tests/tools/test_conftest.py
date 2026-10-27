"""Tests for tests/tools/conftest.py: the gdtoolkit cache folder exists before the parser needs it."""

from __future__ import annotations

import importlib.metadata
import importlib.util
import sys
from pathlib import Path
from types import ModuleType

import pytest

CONFTEST_PATH = Path(__file__).resolve().parent / "conftest.py"


def _load_conftest() -> ModuleType:
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("tests_tools_conftest", CONFTEST_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


conftest = _load_conftest()


def test_the_gdtoolkit_cache_folder_is_made_before_the_parser_needs_it(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    if sys.platform == "win32":
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    else:
        monkeypatch.setenv("HOME", str(tmp_path))

    folder = conftest.make_gdtoolkit_cache_folder()

    assert folder is not None
    assert folder.is_dir()
    assert folder.is_relative_to(tmp_path)
    assert folder.parts[-2:] == ("gdtoolkit", importlib.metadata.version("gdtoolkit"))
    assert conftest.make_gdtoolkit_cache_folder() == folder
