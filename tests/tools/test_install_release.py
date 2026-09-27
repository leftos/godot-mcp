"""Tests for tools/install-release.ps1: a release zip installed into temporary folders, under pwsh and Windows PowerShell."""

from __future__ import annotations

import os
import shutil
import stat
import subprocess
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path

import pytest

SCRIPT_PATH = Path(__file__).resolve().parents[2] / "tools" / "install-release.ps1"
VERSION = "9.8.7+abc1234"

pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="the installer, junctions and winget are Windows-only")

SHELLS = [
    "pwsh",
    pytest.param(
        "powershell",
        marks=pytest.mark.skipif(shutil.which("powershell") is None, reason="Windows PowerShell 5.1 is not installed"),
    ),
]


@dataclass(frozen=True)
class Layout:
    zip_path: Path
    install_dir: Path
    skills_dir: Path
    dotnet_root: Path

    @property
    def skill(self) -> Path:
        return self.skills_dir / "godot-mcp"


def _write_zip(path: Path, entries: dict[str, str]) -> Path:
    with zipfile.ZipFile(path, "w") as archive:
        for name, text in entries.items():
            archive.writestr(name, text)
    return path


def _release_entries() -> dict[str, str]:
    return {
        "godot-mcp.exe": "not really an exe",
        "godot-mcp.dll": "not really a dll",
        "VERSION": f"{VERSION}\n",
        "bridge/godot_mcp_bridge.gd": "extends Node\n",
        "skill/SKILL.md": "# godot-mcp\n",
    }


def _layout(tmp_path: Path) -> Layout:
    dotnet_root = tmp_path / "dotnet"
    (dotnet_root / "shared" / "Microsoft.NETCore.App" / "10.0.0").mkdir(parents=True)
    return Layout(
        zip_path=_write_zip(tmp_path / "release.zip", _release_entries()),
        install_dir=tmp_path / "installed",
        skills_dir=tmp_path / "home" / "skills",
        dotnet_root=dotnet_root,
    )


def _environment(dotnet_root: Path) -> dict[str, str]:
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("DOTNET_ROOT")}
    env.pop("GODOT_MCP_INSTALL_DIR", None)
    env.pop("GODOT_MCP_SKILLS_DIR", None)
    env["DOTNET_ROOT"] = str(dotnet_root)
    return env


def _quote(value: str | Path) -> str:
    """A PowerShell single-quoted string literal."""
    return "'" + str(value).replace("'", "''") + "'"


def _invoke(shell: str, target: str, arguments: list[str], env: dict[str, str]) -> subprocess.CompletedProcess[str]:
    """Runs `& <target> <arguments>` in the shell. A stop is printed as its bare message with status 1: both shells
    wrap an uncaught error's message at the console width, which can cut a long path in two."""
    source = f"try {{ & {target} {' '.join(arguments)} }} catch {{ [Console]::Error.WriteLine($_.Exception.Message); exit 1 }}"
    return subprocess.run(
        [shell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", source],
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
        env=env,
    )


def _run(shell: str, layout: Layout, zip_path: Path | None = None) -> subprocess.CompletedProcess[str]:
    arguments = ["-ZipPath", _quote(zip_path or layout.zip_path), "-InstallDir", _quote(layout.install_dir)]
    arguments += ["-SkillsDir", _quote(layout.skills_dir)]
    return _invoke(shell, _quote(SCRIPT_PATH), arguments, _environment(layout.dotnet_root))


def _output(result: subprocess.CompletedProcess[str]) -> str:
    return " ".join((result.stdout + result.stderr).split())


def _is_link(path: Path) -> bool:
    return bool(os.lstat(path).st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def _make_junction(link: Path, target: Path) -> None:
    subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)], check=True, capture_output=True)


@pytest.mark.parametrize("shell", SHELLS)
def test_installs_the_server_and_copies_the_skill(tmp_path: Path, shell: str) -> None:
    layout = _layout(tmp_path)

    result = _run(shell, layout)

    assert result.returncode == 0, result.stdout + result.stderr
    assert (layout.install_dir / "godot-mcp.exe").is_file()
    assert (layout.install_dir / "bridge" / "godot_mcp_bridge.gd").is_file()
    assert (layout.install_dir / "VERSION").read_text(encoding="utf-8").strip() == VERSION
    assert not (layout.install_dir / "skill").exists()
    assert (layout.skill / "SKILL.md").is_file()
    assert not _is_link(layout.skill)
    exe = layout.install_dir / "godot-mcp.exe"
    output = _output(result)
    assert f"install: server at {exe} (version {VERSION}), skill at {layout.skill}" in output
    assert f'claude mcp add godot -s local -e GODOT_PATH=<your Godot console exe> -- "{exe}"' in output


@pytest.mark.parametrize("shell", SHELLS)
def test_a_skill_junction_is_replaced_and_its_target_left_alone(tmp_path: Path, shell: str) -> None:
    layout = _layout(tmp_path)
    scratch = tmp_path / "source-skill"
    scratch.mkdir()
    (scratch / "sentinel.txt").write_text("keep me", encoding="utf-8")
    layout.skills_dir.mkdir(parents=True)
    _make_junction(layout.skill, scratch)

    result = _run(shell, layout)

    assert result.returncode == 0, result.stdout + result.stderr
    assert (scratch / "sentinel.txt").read_text(encoding="utf-8") == "keep me"
    assert not _is_link(layout.skill)
    assert (layout.skill / "SKILL.md").is_file()
    assert not (layout.skill / "sentinel.txt").exists()


@pytest.mark.parametrize("shell", SHELLS)
def test_runs_as_a_scriptblock_with_no_file_beside_it(tmp_path: Path, shell: str) -> None:
    layout = _layout(tmp_path)
    target = f"([scriptblock]::Create((Get-Content -Raw -LiteralPath {_quote(SCRIPT_PATH)})))"
    arguments = ["-ZipPath", _quote(layout.zip_path), "-InstallDir", _quote(layout.install_dir)]
    arguments += ["-SkillsDir", _quote(layout.skills_dir)]

    result = _invoke(shell, target, arguments, _environment(layout.dotnet_root))

    assert result.returncode == 0, result.stdout + result.stderr
    assert (layout.install_dir / "VERSION").read_text(encoding="utf-8").strip() == VERSION
    assert (layout.skill / "SKILL.md").is_file()


@pytest.mark.parametrize("shell", SHELLS)
def test_a_stop_run_as_a_file_exits_with_status_1(tmp_path: Path, shell: str) -> None:
    layout = _layout(tmp_path)
    shutil.rmtree(layout.dotnet_root / "shared")
    command = [shell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT_PATH), "-ZipPath", str(layout.zip_path)]
    command += ["-InstallDir", str(layout.install_dir), "-SkillsDir", str(layout.skills_dir)]

    result = subprocess.run(
        command,
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
        env=_environment(layout.dotnet_root),
    )

    assert result.returncode == 1, result.stdout + result.stderr
    assert not layout.install_dir.exists()


@pytest.mark.parametrize("shell", SHELLS)
def test_a_missing_runtime_without_a_console_stops_naming_install_dotnet(tmp_path: Path, shell: str) -> None:
    layout = _layout(tmp_path)
    shutil.rmtree(layout.dotnet_root / "shared")

    result = _run(shell, layout)

    assert result.returncode != 0
    output = _output(result)
    assert "the .NET 10 runtime (Microsoft.NETCore.App 10.*) is missing" in output
    assert "-InstallDotNet" in output
    assert "https://dotnet.microsoft.com/download/dotnet/10.0" in output
    assert not layout.install_dir.exists()


def test_a_reinstall_replaces_the_install_folders_contents(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    assert _run("pwsh", layout).returncode == 0
    (layout.install_dir / "stale.dll").write_text("from an older release", encoding="utf-8")

    result = _run("pwsh", layout)

    assert result.returncode == 0, result.stdout + result.stderr
    assert not (layout.install_dir / "stale.dll").exists()
    assert (layout.install_dir / "godot-mcp.exe").is_file()


def test_a_folder_that_is_not_an_install_is_refused_and_left_alone(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    layout.install_dir.mkdir()
    (layout.install_dir / "notes.txt").write_text("mine", encoding="utf-8")

    result = _run("pwsh", layout)

    assert result.returncode != 0
    assert "is not a godot-mcp install; it is left alone" in _output(result)
    assert (layout.install_dir / "notes.txt").read_text(encoding="utf-8") == "mine"
    assert not (layout.install_dir / "godot-mcp.exe").exists()


def test_a_file_that_is_not_a_zip_is_refused(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    broken = tmp_path / "broken.zip"
    broken.write_bytes(b"not a zip at all")

    result = _run("pwsh", layout, zip_path=broken)

    assert result.returncode != 0
    assert f"install: {broken} could not be unpacked" in _output(result)
    assert not layout.install_dir.exists()


def test_a_zip_without_the_skill_is_refused(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    entries = _release_entries()
    del entries["skill/SKILL.md"]
    partial = _write_zip(tmp_path / "partial.zip", entries)

    result = _run("pwsh", layout, zip_path=partial)

    assert result.returncode != 0
    assert "is not a godot-mcp release: it has no skill/SKILL.md" in _output(result)
    assert not layout.install_dir.exists()


def test_a_missing_zip_path_is_refused(tmp_path: Path) -> None:
    layout = _layout(tmp_path)

    result = _run("pwsh", layout, zip_path=tmp_path / "nowhere.zip")

    assert result.returncode != 0
    assert f"install: -ZipPath {tmp_path / 'nowhere.zip'} does not exist." in _output(result)


def test_a_version_that_is_not_x_y_z_is_refused_before_any_download(tmp_path: Path) -> None:
    layout = _layout(tmp_path)
    arguments = ["-Version", "latest", "-InstallDir", _quote(layout.install_dir), "-SkillsDir", _quote(layout.skills_dir)]

    result = _invoke("pwsh", _quote(SCRIPT_PATH), arguments, _environment(layout.dotnet_root))

    assert result.returncode != 0
    assert not layout.install_dir.exists()
    assert "-Version 'latest' is not a version; give one as X.Y.Z" in _output(result)
