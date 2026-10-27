"""Prepares shared state before pytest's xdist workers start: gdtoolkit's cache folder."""

from __future__ import annotations

import importlib.metadata
import importlib.util
from pathlib import Path

import pytest


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
