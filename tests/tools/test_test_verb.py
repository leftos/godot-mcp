"""Tests for run.ps1 test, the unit-test verb: what it refuses before it builds anything."""

from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"
METHOD_NEEDS_FILTER = "test -Method needs -Filter: the method is looked up in the classes -Filter names."


def test_the_test_verb_refuses_a_method_with_no_filter(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    root.mkdir()
    shutil.copyfile(RUN_PS1, root / "run.ps1")

    command = ["pwsh", "-NoProfile", "-File", str(root / "run.ps1"), "test", "-Method", "AMethod"]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)

    assert result.returncode == 2, result.stdout + result.stderr
    assert METHOD_NEEDS_FILTER in result.stderr
