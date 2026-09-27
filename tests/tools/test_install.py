"""Tests for tools/install.ps1: the mirror of bin/publish and the skill junction, run against temporary folders."""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

import pytest

SCRIPT_PATH = Path(__file__).resolve().parents[2] / "tools" / "install.ps1"
# Any PE file with a version resource stands in for the published server dll, whose ProductVersion install.ps1 records.
VERSIONED_DLL = Path(os.environ.get("SYSTEMROOT", r"C:\Windows")) / "System32" / "kernel32.dll"

pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="junctions and robocopy are Windows-only")


@dataclass(frozen=True)
class Layout:
    root: Path
    install_dir: Path
    skills_dir: Path

    @property
    def skill_source(self) -> Path:
        return self.root / "skills" / "godot-mcp"

    @property
    def publish_dll(self) -> Path:
        return self.root / "bin" / "publish" / "godot-mcp.dll"

    @property
    def link(self) -> Path:
        return self.skills_dir / "godot-mcp"


def _layout(tmp_path: Path, *, with_skill: bool = True) -> Layout:
    layout = Layout(root=tmp_path / "repo", install_dir=tmp_path / "installed", skills_dir=tmp_path / "home" / "skills")
    publish = layout.root / "bin" / "publish"
    publish.mkdir(parents=True)
    (publish / "godot-mcp.exe").write_text("not really an exe", encoding="utf-8")
    shutil.copyfile(VERSIONED_DLL, layout.publish_dll)
    if with_skill:
        layout.skill_source.mkdir(parents=True)
        (layout.skill_source / "SKILL.md").write_text("# godot-mcp\n", encoding="utf-8")
    layout.skills_dir.mkdir(parents=True)
    return layout


def _install(layout: Layout) -> subprocess.CompletedProcess[str]:
    command = [
        "pwsh",
        "-NoProfile",
        "-File",
        str(SCRIPT_PATH),
        "-Root",
        str(layout.root),
        "-InstallDir",
        str(layout.install_dir),
        "-SkillsDir",
        str(layout.skills_dir),
    ]
    return subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)


def _output(result: subprocess.CompletedProcess[str]) -> str:
    return result.stdout + result.stderr


def _product_version(path: Path) -> str:
    command = ["pwsh", "-NoProfile", "-Command", f"(Get-Item -LiteralPath '{path}').VersionInfo.ProductVersion"]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=True)
    return result.stdout.strip()


def _make_junction(link: Path, target: Path) -> None:
    subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)], capture_output=True, check=True)


def _points_at(link: Path, target: Path) -> bool:
    return os.path.isjunction(link) and os.path.realpath(link).lower() == os.path.realpath(target).lower()


def test_install_mirrors_publish_and_links_skill(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    result = _install(layout)
    assert result.returncode == 0, _output(result)
    assert (layout.install_dir / "godot-mcp.exe").read_text(encoding="utf-8") == "not really an exe"
    assert _points_at(layout.link, layout.skill_source)
    version = _product_version(layout.publish_dll)
    expected = f"install: server at {layout.install_dir / 'godot-mcp.exe'} (version {version}), skill linked at {layout.link}"
    assert expected in result.stdout


def test_install_writes_the_version_file(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    # In System32 the dll's version comes from its .mui file and a copy's does not, so read the copy install reads.
    version = _product_version(layout.publish_dll)
    assert version
    first = _install(layout)
    assert first.returncode == 0, _output(first)
    assert (layout.install_dir / "VERSION").read_bytes() == f"{version}\n".encode()
    second = _install(layout)
    assert second.returncode == 0, _output(second)
    assert (layout.install_dir / "VERSION").read_bytes() == f"{version}\n".encode()
    assert not (layout.root / "bin" / "publish" / "VERSION").exists()


def test_install_is_idempotent(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    first = _install(layout)
    assert first.returncode == 0, _output(first)
    second = _install(layout)
    assert second.returncode == 0, _output(second)
    assert _points_at(layout.link, layout.skill_source)
    assert (layout.install_dir / "godot-mcp.exe").is_file()


def test_install_replaces_a_junction_pointing_elsewhere(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    elsewhere = tmp_path / "old-worktree" / "skills" / "godot-mcp"
    elsewhere.mkdir(parents=True)
    (elsewhere / "SKILL.md").write_text("# old\n", encoding="utf-8")
    _make_junction(layout.link, elsewhere)
    result = _install(layout)
    assert result.returncode == 0, _output(result)
    assert _points_at(layout.link, layout.skill_source)
    assert (elsewhere / "SKILL.md").read_text(encoding="utf-8") == "# old\n"


def test_install_refuses_a_real_folder_at_the_link(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    layout.link.mkdir()
    (layout.link / "notes.md").write_text("mine\n", encoding="utf-8")
    result = _install(layout)
    assert result.returncode == 1
    assert f"install: {layout.link} exists and is not a junction; move it away and run install again." in _output(result)
    assert not os.path.isjunction(layout.link)
    assert (layout.link / "notes.md").read_text(encoding="utf-8") == "mine\n"


def test_install_fails_without_the_skill(tmp_path: Path) -> None:
    layout = _layout(tmp_path, with_skill=False)
    result = _install(layout)
    assert result.returncode == 1
    assert "install: skills/godot-mcp/SKILL.md is missing." in _output(result)
    assert not layout.link.exists()
