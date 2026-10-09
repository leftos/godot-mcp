"""Tests for run.ps1 itest-groups: the itest groups the changes since a ref touch, run in a temporary git repo holding a copy of run.ps1."""

from __future__ import annotations

import re
import shutil
import subprocess
from pathlib import Path

import pytest

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"
ALL_GROUPS = ["lifecycle", "sessions", "input", "reads", "time", "prep", "recording", "capture", "headless", "scene", "nodes", "csharp", "scratch"]
GROUP_LINE = re.compile(r"^itest-groups: (\w+) \(from (.+)\)$", re.MULTILINE)
# The files the repo holds at its first commit; each test changes some of them, or adds a file, after it.
STUBS = [
    "bridge/godot_mcp_bridge.gd",
    "headless/operations.gd",
    "src/GodotMcp.Server/Tools/RuntimeTools.Time.cs",
    "docs/x.md",
    "tools/package.ps1",
    "notes.txt",
]


def _git(repo: Path, *args: str) -> None:
    command = ["git", "-C", str(repo), "-c", "user.name=test", "-c", "user.email=test@example.invalid", *args]
    subprocess.run(command, capture_output=True, text=True, timeout=60, check=True)


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    root = tmp_path / "repo"
    root.mkdir()
    shutil.copyfile(RUN_PS1, root / "run.ps1")
    for stub in STUBS:
        path = root / stub
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("stub\n", encoding="utf-8", newline="\n")
    _git(root, "init", "--quiet")
    _git(root, "add", "--all")
    _git(root, "commit", "--quiet", "-m", "stubs")
    return root


def _change(repo: Path, relative: str) -> None:
    path = repo / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("changed\n", encoding="utf-8", newline="\n")


def _itest_groups(repo: Path, since: str = "HEAD") -> subprocess.CompletedProcess[str]:
    command = ["pwsh", "-NoProfile", "-File", str(repo / "run.ps1"), "itest-groups", "-Since", since]
    return subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)


def _selected(result: subprocess.CompletedProcess[str]) -> list[str]:
    assert result.returncode == 0, result.stdout + result.stderr
    return [match.group(1) for match in GROUP_LINE.finditer(result.stdout)]


def test_a_bridge_file_selects_every_group(repo: Path) -> None:
    _change(repo, "bridge/godot_mcp_bridge.gd")
    result = _itest_groups(repo)
    assert _selected(result) == ALL_GROUPS
    assert "itest-groups: csharp (from bridge/godot_mcp_bridge.gd)" in result.stdout
    assert f"itest-groups: {len(ALL_GROUPS)} of {len(ALL_GROUPS)} groups from 1 changed files" in result.stdout


def test_a_headless_file_selects_the_headless_groups_and_reads(repo: Path) -> None:
    _change(repo, "headless/operations.gd")
    assert _selected(_itest_groups(repo)) == ["reads", "headless", "scene", "nodes"]


def test_a_runtime_tools_part_selects_its_group(repo: Path) -> None:
    _change(repo, "src/GodotMcp.Server/Tools/RuntimeTools.Time.cs")
    assert _selected(_itest_groups(repo)) == ["time", "scratch"]


def test_a_scratch_tools_part_selects_the_scratch_group(repo: Path) -> None:
    _change(repo, "src/GodotMcp.Server/Tools/ScratchTools.cs")
    assert _selected(_itest_groups(repo)) == ["scratch"]


def test_a_state_file_beside_the_runtime_tools_selects_reads(repo: Path) -> None:
    _change(repo, "src/GodotMcp.Server/Tools/StateMerge.cs")
    assert _selected(_itest_groups(repo)) == ["reads"]


def test_docs_alone_select_no_group(repo: Path) -> None:
    _change(repo, "docs/x.md")
    result = _itest_groups(repo)
    assert _selected(result) == []
    assert f"itest-groups: 0 of {len(ALL_GROUPS)} groups from 1 changed files" in result.stdout


def test_a_dev_only_tool_alone_selects_no_group(repo: Path) -> None:
    _change(repo, "tools/package.ps1")
    result = _itest_groups(repo)
    assert _selected(result) == []
    assert "unmapped" not in result.stdout


def test_an_unmapped_file_selects_every_group_and_is_named(repo: Path) -> None:
    _change(repo, "notes.txt")
    result = _itest_groups(repo)
    assert _selected(result) == ALL_GROUPS
    assert "itest-groups: notes.txt is unmapped: no rule maps it, so it runs every group" in result.stdout


def test_an_untracked_test_class_file_selects_the_group_listing_it(repo: Path) -> None:
    _change(repo, "tests/GodotMcp.IntegrationTests/InputTests.cs")
    result = _itest_groups(repo)
    assert _selected(result) == ["input"]
    assert "itest-groups: input (from tests/GodotMcp.IntegrationTests/InputTests.cs)" in result.stdout


def test_an_unknown_ref_stops_with_status_2_and_gits_message(repo: Path) -> None:
    result = _itest_groups(repo, since="no-such-ref")
    assert result.returncode == 2, result.stdout + result.stderr
    assert "no-such-ref" in result.stderr
    assert GROUP_LINE.search(result.stdout) is None


def _itest(repo: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
    command = ["pwsh", "-NoProfile", "-File", str(repo / "run.ps1"), "itest", *arguments]
    return subprocess.run(command, capture_output=True, text=True, timeout=120, check=False)


def test_a_filter_spanning_the_visible_and_hidden_groups_is_refused(repo: Path) -> None:
    result = _itest(repo, "-Filter", "*Tests")

    assert result.returncode == 2, result.stdout + result.stderr
    refusal = "itest -Filter '*Tests' selects classes of the visible-desktop group capture and of hidden-desktop groups; filter them apart."
    assert refusal in result.stderr
