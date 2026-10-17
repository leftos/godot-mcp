"""Tests for tools/copy-skills.ps1: the skill folders and docs/TOOLS.md a publish ships, run against temporary checkouts."""

from __future__ import annotations

import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

import pytest

CHECKOUT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = CHECKOUT / "tools" / "copy-skills.ps1"
TOOLS_URL = "https://github.com/leftos/godot-mcp/blob/main/docs"

pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="the skill layout a publish ships is a Windows artifact")

TOOLS_MD = """# Using the godot-mcp tools

Words such as session and quiet run are defined in the [glossary](./README.md#glossary).
The tool table is in [ARCHITECTURE.md](./ARCHITECTURE.md#tools).
"""


@dataclass(frozen=True)
class Layout:
    root: Path
    destination: Path

    @property
    def skill(self) -> Path:
        return self.destination / "skill"

    @property
    def sweep_skill(self) -> Path:
        return self.destination / "agent-sweep-skill"


def _layout(
    tmp_path: Path,
    *,
    skill_text: str = "# godot-mcp\n",
    with_skill: bool = True,
    with_sweep_skill: bool = True,
    tools_text: str | None = TOOLS_MD,
) -> Layout:
    layout = Layout(root=tmp_path / "repo", destination=tmp_path / "publish")
    for name, present in (("godot-mcp", with_skill), ("godot-agent-sweep", with_sweep_skill)):
        folder = layout.root / "skills" / name
        folder.mkdir(parents=True)
        if present:
            text = skill_text if name == "godot-mcp" else f"# {name}\n"
            (folder / "SKILL.md").write_text(text, encoding="utf-8", newline="\n")
    (layout.root / "docs").mkdir()
    if tools_text is not None:
        (layout.root / "docs" / "TOOLS.md").write_text(tools_text, encoding="utf-8", newline="\n")
    return layout


def _run(root: Path, destination: Path) -> subprocess.CompletedProcess[str]:
    command = [
        "pwsh",
        "-NoProfile",
        "-File",
        str(SCRIPT_PATH),
        "-Root",
        str(root),
        "-Destination",
        str(destination),
    ]
    return subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)


def _run_layout(layout: Layout) -> subprocess.CompletedProcess[str]:
    return _run(layout.root, layout.destination)


def _shipped_tools() -> str:
    """TOOLS.md as the publish writes it: its links to sibling docs point at their GitHub copy, everything else is kept."""
    expected = TOOLS_MD
    for name in ("README.md#glossary", "ARCHITECTURE.md#tools"):
        expected = expected.replace(f"](./{name})", f"]({TOOLS_URL}/{name})")
    return expected


def test_copies_both_skills_and_rewrites_tools_links(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    result = _run_layout(layout)
    assert result.returncode == 0, result.stderr
    assert (layout.skill / "SKILL.md").read_bytes() == b"# godot-mcp\n"
    assert (layout.sweep_skill / "SKILL.md").read_bytes() == b"# godot-agent-sweep\n"
    # UTF-8 without a BOM, LF line endings, and every byte but the rewritten links unchanged.
    assert (layout.skill / "TOOLS.md").read_bytes() == _shipped_tools().encode("utf-8")


def test_a_stale_destination_folder_is_replaced(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    layout.skill.mkdir(parents=True)
    (layout.skill / "STALE.md").write_text("from an earlier publish\n", encoding="utf-8")
    result = _run_layout(layout)
    assert result.returncode == 0, result.stderr
    assert not (layout.skill / "STALE.md").exists()
    assert (layout.skill / "SKILL.md").is_file()


def test_a_missing_skill_file_is_refused(tmp_path: Path) -> None:
    layout = _layout(tmp_path, with_skill=False)
    result = _run_layout(layout)
    assert result.returncode == 1
    assert "copy-skills: skills/godot-mcp/SKILL.md is missing." in result.stderr
    assert not layout.destination.exists()


def test_a_missing_tools_doc_is_refused(tmp_path: Path) -> None:
    layout = _layout(tmp_path, tools_text=None)
    result = _run_layout(layout)
    assert result.returncode == 1
    assert "copy-skills: docs/TOOLS.md is missing." in result.stderr
    assert not layout.destination.exists()


def test_an_absolute_path_in_a_skill_is_refused(tmp_path: Path) -> None:
    layout = _layout(tmp_path, skill_text="# godot-mcp\n\nX:\\dev\\godot-mcp\\docs\\TOOLS.md\n")
    result = _run_layout(layout)
    assert result.returncode == 1
    assert "copy-skills: skills/godot-mcp/SKILL.md:3: an absolute path (X:\\dev\\godot-mcp\\docs\\TOOLS.md)" in result.stderr
    assert "ships with the skill; name it relatively or by URL." in result.stderr
    assert not layout.destination.exists()


def test_an_absolute_path_in_tools_is_refused(tmp_path: Path) -> None:
    layout = _layout(tmp_path, tools_text="# Tools\n\nC:/Users/lefto/notes.md\n")
    result = _run_layout(layout)
    assert result.returncode == 1
    assert "copy-skills: docs/TOOLS.md:3: an absolute path (C:/Users/lefto/notes.md)" in result.stderr
    assert not layout.destination.exists()


def test_urls_and_colon_text_pass(tmp_path: Path) -> None:
    layout = _layout(
        tmp_path,
        skill_text="# godot-mcp\n\nhttps://github.com/leftos/godot-mcp/blob/main/docs/TOOLS.md\na:b\n",
    )
    result = _run_layout(layout)
    assert result.returncode == 0, result.stderr


def test_the_checkout_ships(tmp_path: Path) -> None:
    destination = tmp_path / "publish"
    result = _run(CHECKOUT, destination)
    assert result.returncode == 0, result.stderr
    assert (destination / "skill" / "TOOLS.md").is_file()
    assert (destination / "skill" / "SKILL.md").read_bytes() == (CHECKOUT / "skills" / "godot-mcp" / "SKILL.md").read_bytes()
