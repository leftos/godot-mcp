"""Tests for tools/install-release.ps1: a release zip installed into temporary folders, under pwsh and Windows PowerShell."""

from __future__ import annotations

import dataclasses
import os
import shutil
import stat
import subprocess
import sys
import zipfile
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path

import pytest
from conftest import SHELLS
from fake_installed_server import PROJECT, SESSION_ID, start_fake_server, start_process, write_transcript

TOOLS = Path(__file__).resolve().parents[2] / "tools"
SCRIPT_PATH = TOOLS / "install-release.ps1"
VERSION = "9.8.7+abc1234"
FOLDER = "9.8.7-abc1234"
LAUNCHER_TEXT = "launcher v1"
# Any PE file with a version resource stands in for the published server dll, whose ProductVersion package.ps1 records.
VERSIONED_DLL = Path(os.environ.get("SYSTEMROOT", r"C:\Windows")) / "System32" / "kernel32.dll"
# The stand-in server is framework-dependent, so it starts only where the installer finds a real .NET 10 runtime.
DOTNET_ROOT = Path(shutil.which("dotnet") or "dotnet").resolve().parent
# A hang guard only: a passing run never comes near it.
HANG_GUARD_SECONDS = 120

pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="the installer, junctions and winget are Windows-only")


@dataclass(frozen=True)
class Layout:
    zip_path: Path
    install_dir: Path
    skills_dir: Path
    dotnet_root: Path

    @property
    def skill(self) -> Path:
        return self.skills_dir / "godot-mcp"

    @property
    def sweep_skill(self) -> Path:
        return self.skills_dir / "godot-agent-sweep"

    @property
    def launcher(self) -> Path:
        return self.install_dir / "godot-mcp.exe"

    @property
    def versions(self) -> Path:
        return self.install_dir / "versions"

    def current(self) -> str:
        return (self.install_dir / "current").read_text(encoding="utf-8").splitlines()[0]

    def version_folders(self) -> list[str]:
        return sorted(path.name for path in self.versions.iterdir())


def _write_zip(path: Path, entries: Mapping[str, str | bytes]) -> Path:
    with zipfile.ZipFile(path, "w") as archive:
        for name, text in entries.items():
            archive.writestr(name, text)
    return path


def _release_entries(stand_in: Path) -> dict[str, str | bytes]:
    return {
        "godot-mcp.exe": stand_in.read_bytes(),
        "godot-mcp-launcher.exe": LAUNCHER_TEXT,
        "godot-mcp.dll": "not really a dll",
        "VERSION": f"{VERSION}\n",
        "bridge/godot_mcp_bridge.gd": "extends Node\n",
        "capture/godot-mcp-capture.exe": "not really an exe",
        "skill/SKILL.md": "# godot-mcp\n",
        "agent-sweep-skill/SKILL.md": "# godot-agent-sweep\n",
    }


def _layout(tmp_path: Path, stand_in: Path) -> Layout:
    return Layout(
        zip_path=_write_zip(tmp_path / "release.zip", _release_entries(stand_in)),
        install_dir=tmp_path / "installed",
        skills_dir=tmp_path / "home" / "skills",
        dotnet_root=DOTNET_ROOT,
    )


@pytest.fixture
def layout(tmp_path: Path, stand_in: Path) -> Layout:
    return _layout(tmp_path, stand_in)


def _without_runtime(tmp_path: Path, layout: Layout) -> Layout:
    """The layout with a .NET root that holds no runtime."""
    empty = tmp_path / "dotnet"
    empty.mkdir()
    return dataclasses.replace(layout, dotnet_root=empty)


def _environment(dotnet_root: Path, home: Path | None = None) -> dict[str, str]:
    """The installer's environment; with a home, USERPROFILE points there, where it looks for Claude Code's transcripts."""
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("DOTNET_ROOT")}
    env.pop("GODOT_MCP_INSTALL_DIR", None)
    env.pop("GODOT_MCP_SKILLS_DIR", None)
    env["DOTNET_ROOT"] = str(dotnet_root)
    if home is not None:
        env["USERPROFILE"] = str(home)
    return env


def _publish_layout(root: Path, *, capture_helper: bool = True) -> Path:
    """A stand-in bin/publish holding everything package.ps1 asserts, with the capture helper left out when asked."""
    publish = root / "bin" / "publish"
    for folder in ("bridge", "headless", "dotnet", "capture", "launcher"):
        (publish / folder).mkdir(parents=True)
    (publish / "godot-mcp.exe").write_text("not really an exe", encoding="utf-8")
    (publish / "launcher" / "godot-mcp.exe").write_text(LAUNCHER_TEXT, encoding="utf-8")
    (publish / "launcher" / "godot-mcp.pdb").write_text("the launcher's symbols", encoding="utf-8")
    if capture_helper:
        (publish / "capture" / "godot-mcp-capture.exe").write_text("not really an exe", encoding="utf-8")
    shutil.copyfile(VERSIONED_DLL, publish / "godot-mcp.dll")
    for name in ("godot-mcp", "godot-agent-sweep"):
        skill = root / "skills" / name
        skill.mkdir(parents=True)
        (skill / "SKILL.md").write_text(f"# {name}\n", encoding="utf-8")
    return publish


def _package(root: Path, output: Path) -> subprocess.CompletedProcess[str]:
    """Runs tools/package.ps1 against a stand-in root, as the release build does."""
    command = ["pwsh", "-NoProfile", "-File", str(TOOLS / "package.ps1"), "-Root", str(root), "-OutputDir", str(output)]
    return subprocess.run(command, capture_output=True, text=True, timeout=HANG_GUARD_SECONDS, check=False)


@pytest.fixture(scope="module")
def packaged(tmp_path_factory: pytest.TempPathFactory) -> Path:
    """The folder tools/package.ps1 writes the release zip and its install.ps1 into, packaged from a stand-in publish."""
    root = tmp_path_factory.mktemp("package-root")
    _publish_layout(root)
    output = root / "package"
    result = _package(root, output)
    assert result.returncode == 0, result.stdout + result.stderr
    assert f"package: {output / 'install.ps1'}" in result.stdout
    return output


@pytest.fixture(scope="module")
def published_installer(packaged: Path) -> Path:
    """The install.ps1 tools/package.ps1 writes beside the release zip."""
    return packaged / "install.ps1"


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
        timeout=HANG_GUARD_SECONDS,
        check=False,
        env=env,
    )


def _run(
    shell: str,
    layout: Layout,
    zip_path: Path | None = None,
    home: Path | None = None,
    extra: Mapping[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    arguments = ["-ZipPath", _quote(zip_path or layout.zip_path), "-InstallDir", _quote(layout.install_dir)]
    arguments += ["-SkillsDir", _quote(layout.skills_dir)]
    env = _environment(layout.dotnet_root, home)
    env.update(extra or {})
    return _invoke(shell, _quote(SCRIPT_PATH), arguments, env)


def _ran(shell: str, layout: Layout, home: Path | None = None) -> subprocess.CompletedProcess[str]:
    result = _run(shell, layout, home=home)
    assert result.returncode == 0, result.stdout + result.stderr
    return result


def _run_as_scriptblock(shell: str, layout: Layout, script: Path, home: Path | None = None) -> subprocess.CompletedProcess[str]:
    """Runs the script the way the release's one-line install does: as a scriptblock, with no file beside it."""
    target = f"([scriptblock]::Create((Get-Content -Raw -LiteralPath {_quote(script)})))"
    arguments = ["-ZipPath", _quote(layout.zip_path), "-InstallDir", _quote(layout.install_dir)]
    arguments += ["-SkillsDir", _quote(layout.skills_dir)]
    return _invoke(shell, target, arguments, _environment(layout.dotnet_root, home))


def _output(result: subprocess.CompletedProcess[str]) -> str:
    return " ".join((result.stdout + result.stderr).split())


def _is_link(path: Path) -> bool:
    return bool(os.lstat(path).st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def _make_junction(link: Path, target: Path) -> None:
    subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)], check=True, capture_output=True)


def _tree(folder: Path) -> dict[str, bytes]:
    return {str(path.relative_to(folder)): path.read_bytes() for path in folder.rglob("*") if path.is_file()}


@pytest.mark.parametrize("shell", SHELLS)
def test_installs_the_launcher_a_version_folder_and_the_skills(layout: Layout, shell: str) -> None:
    result = _ran(shell, layout)

    folder = layout.versions / FOLDER
    assert layout.current() == FOLDER
    assert layout.launcher.read_text(encoding="utf-8") == LAUNCHER_TEXT
    assert (folder / "bridge" / "godot_mcp_bridge.gd").is_file()
    assert not (folder / "godot-mcp-launcher.exe").exists()
    assert (folder / "skill" / "SKILL.md").is_file()
    assert (folder / "VERSION").read_text(encoding="utf-8").strip() == VERSION
    assert (layout.install_dir / "VERSION").read_text(encoding="utf-8").strip() == VERSION
    assert sorted(path.name for path in layout.install_dir.iterdir()) == ["VERSION", "current", "godot-mcp.exe", "install.lock", "versions"]
    assert (layout.skill / "SKILL.md").is_file()
    assert not _is_link(layout.skill)
    assert (layout.sweep_skill / "SKILL.md").read_text(encoding="utf-8") == "# godot-agent-sweep\n"
    assert not _is_link(layout.sweep_skill)
    output = _output(result)
    assert f"install: server at {layout.launcher} (version {VERSION}), skill at {layout.skill}" in output
    assert f"install: agent sweep skill at {layout.sweep_skill}" in output
    assert f'claude mcp add godot -s local -e GODOT_PATH=<your Godot console exe> -- "{layout.launcher}"' in output
    assert "stand-in: swept the agent files" in output


def test_a_sweep_that_exits_non_zero_fails_the_install_but_undoes_nothing(layout: Layout) -> None:
    result = _run("pwsh", layout, extra={"STANDIN_SWEEP_STATUS": "3"})

    assert result.returncode != 0
    assert "install: the agent sweep exited with status 3" in _output(result)
    assert layout.current() == FOLDER
    assert (layout.skill / "SKILL.md").is_file()
    assert (layout.sweep_skill / "SKILL.md").is_file()


@pytest.mark.parametrize("shell", SHELLS)
def test_a_skill_junction_is_replaced_and_its_target_left_alone(tmp_path: Path, layout: Layout, shell: str) -> None:
    scratch = tmp_path / "source-skill"
    scratch.mkdir()
    (scratch / "sentinel.txt").write_text("keep me", encoding="utf-8")
    layout.skills_dir.mkdir(parents=True)
    _make_junction(layout.skill, scratch)

    _ran(shell, layout)

    assert (scratch / "sentinel.txt").read_text(encoding="utf-8") == "keep me"
    assert not _is_link(layout.skill)
    assert (layout.skill / "SKILL.md").is_file()
    assert not (layout.skill / "sentinel.txt").exists()


def test_the_published_installer_carries_both_modules_inline(published_installer: Path) -> None:
    text = published_installer.read_text(encoding="utf-8")

    for name in ("InstalledServers", "InstallLayout"):
        module = (TOOLS / f"{name}.psm1").read_text(encoding="utf-8")
        assert module.strip() in text
        assert text.count(f"#region {name}") == 1
    assert "function Stop-InstalledServer" in text
    assert "function Set-CurrentVersion" in text
    assert "Import-Module" not in text
    assert "\r\n" not in text


def test_the_package_puts_the_launcher_at_the_zip_root_without_its_symbols(packaged: Path) -> None:
    with zipfile.ZipFile(next(packaged.glob("godot-mcp-*-win-x64.zip"))) as archive:
        names = archive.namelist()
        launcher = archive.read("godot-mcp-launcher.exe").decode()

    assert launcher == LAUNCHER_TEXT
    assert not [name for name in names if name.startswith("launcher")]
    assert not [name for name in names if name.endswith(".pdb")]
    assert "godot-mcp.exe" in names


@pytest.mark.parametrize("shell", SHELLS)
def test_the_published_installer_runs_as_a_scriptblock_with_no_file_beside_it(layout: Layout, shell: str, published_installer: Path) -> None:
    result = _run_as_scriptblock(shell, layout, published_installer)

    assert result.returncode == 0, result.stdout + result.stderr
    assert (layout.install_dir / "VERSION").read_text(encoding="utf-8").strip() == VERSION
    assert layout.current() == FOLDER
    assert (layout.skill / "SKILL.md").is_file()


def _stopped_line(pid: int) -> str:
    return f"install: stopped the godot-mcp server (pid {pid}) of Claude session {SESSION_ID} in {PROJECT}; run /mcp there to reconnect it"


def _flat_install(layout: Layout) -> None:
    """Today's flat folder: the server's files straight in the install folder, no pointer."""
    layout.install_dir.mkdir(parents=True)
    (layout.install_dir / "godot-mcp.dll").write_text("the flat dll", encoding="utf-8")
    (layout.install_dir / "bridge").mkdir()
    (layout.install_dir / "bridge" / "godot_mcp_bridge.gd").write_text("extends Node\n", encoding="utf-8")
    (layout.install_dir / "VERSION").write_text("0.1.0+abcdef0\n", encoding="utf-8")


def _assert_migrated(layout: Layout) -> None:
    assert sorted(path.name for path in layout.install_dir.iterdir()) == ["VERSION", "current", "godot-mcp.exe", "install.lock", "versions"]
    assert layout.launcher.read_text(encoding="utf-8") == LAUNCHER_TEXT
    assert layout.version_folders() == [FOLDER]


@pytest.mark.parametrize("shell", SHELLS)
def test_a_flat_install_with_a_running_server_is_stopped_named_and_migrated(tmp_path: Path, layout: Layout, shell: str) -> None:
    home = tmp_path / "userprofile"
    write_transcript(home, SESSION_ID)
    _flat_install(layout)
    server = start_fake_server(layout.install_dir, session_id=SESSION_ID)
    try:
        result = _ran(shell, layout, home=home)

        assert not server.is_running()
        assert _stopped_line(server.pid) in _output(result)
        _assert_migrated(layout)
    finally:
        server.stop()


@pytest.mark.parametrize("shell", SHELLS)
def test_the_published_installer_stops_a_running_flat_server_and_names_its_claude_session(
    tmp_path: Path, layout: Layout, shell: str, published_installer: Path
) -> None:
    home = tmp_path / "userprofile"
    write_transcript(home, SESSION_ID)
    _flat_install(layout)
    server = start_fake_server(layout.install_dir, session_id=SESSION_ID)
    try:
        result = _run_as_scriptblock(shell, layout, published_installer, home=home)

        assert result.returncode == 0, result.stdout + result.stderr
        assert not server.is_running()
        assert _stopped_line(server.pid) in _output(result)
        _assert_migrated(layout)
    finally:
        server.stop()


def test_stops_a_flat_server_of_an_unknown_client(tmp_path: Path, layout: Layout) -> None:
    _flat_install(layout)
    server = start_fake_server(layout.install_dir, session_id=None)
    try:
        result = _ran("pwsh", layout, home=tmp_path / "userprofile")

        assert not server.is_running()
        expected = f"install: stopped the godot-mcp server (pid {server.pid}) of an unknown client; run /mcp there to reconnect it"
        assert expected in _output(result)
    finally:
        server.stop()


def test_a_session_without_a_transcript_is_named_without_a_project(tmp_path: Path, layout: Layout) -> None:
    _flat_install(layout)
    server = start_fake_server(layout.install_dir, session_id=SESSION_ID)
    try:
        result = _ran("pwsh", layout, home=tmp_path / "userprofile")

        expected = f"install: stopped the godot-mcp server (pid {server.pid}) of Claude session {SESSION_ID}; run /mcp there to reconnect it"
        assert expected in _output(result)
    finally:
        server.stop()


@pytest.mark.parametrize("shell", SHELLS)
def test_a_server_running_from_a_version_keeps_it_and_a_later_install_removes_it(tmp_path: Path, layout: Layout, shell: str) -> None:
    home = tmp_path / "userprofile"
    write_transcript(home, SESSION_ID)
    _ran(shell, layout)
    first = layout.versions / FOLDER
    before = _tree(first)
    flag = tmp_path / "server.flag"
    flag.write_text("run", encoding="utf-8")
    server = start_process(first / "godot-mcp.exe", [str(flag)], session_id=SESSION_ID)
    try:
        second = _ran(shell, layout, home=home)
        assert server.is_running()
        assert layout.current() == f"{FOLDER}.2"
        assert _tree(first) == before
        assert f"install: Claude session {SESSION_ID} ({PROJECT}) still runs {VERSION}; /mcp it to use {VERSION}" in _output(second)

        _ran(shell, layout)
        assert server.is_running()
        assert _tree(first) == before
    finally:
        flag.unlink()
        server.parent.wait(timeout=HANG_GUARD_SECONDS)
        server.stop()

    _ran(shell, layout)
    assert layout.version_folders() == [f"{FOLDER}.3", f"{FOLDER}.4"]


def test_a_second_install_of_one_release_gets_the_next_suffix(layout: Layout) -> None:
    _ran("pwsh", layout)
    _ran("pwsh", layout)

    assert layout.version_folders() == [FOLDER, f"{FOLDER}.2"]
    assert layout.current() == f"{FOLDER}.2"


@pytest.mark.parametrize("shell", SHELLS)
def test_a_held_install_lock_waits_then_refuses(layout: Layout, shell: str) -> None:
    layout.install_dir.mkdir()
    lock = layout.install_dir / "install.lock"
    with open(lock, "a"):
        result = _run(shell, layout, extra={"GODOT_MCP_INSTALL_LOCK_WAIT_S": "1"})

    assert result.returncode != 0
    assert f"install: another install holds {lock}; try again once it ends" in _output(result)
    assert not (layout.install_dir / "current").exists()


@pytest.mark.parametrize("shell", SHELLS)
def test_a_zip_from_before_the_launcher_is_refused(tmp_path: Path, stand_in: Path, layout: Layout, shell: str) -> None:
    entries = _release_entries(stand_in)
    del entries["godot-mcp-launcher.exe"]
    old = _write_zip(tmp_path / "old.zip", entries)

    result = _run(shell, layout, zip_path=old)

    assert result.returncode != 0
    assert f"install: {VERSION} predates the launcher; install it with that release's own install.ps1" in _output(result)
    assert not layout.install_dir.exists()


@pytest.mark.parametrize("shell", SHELLS)
def test_a_stop_run_as_a_file_exits_with_status_1(tmp_path: Path, layout: Layout, shell: str) -> None:
    layout = _without_runtime(tmp_path, layout)
    command = [shell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT_PATH), "-ZipPath", str(layout.zip_path)]
    command += ["-InstallDir", str(layout.install_dir), "-SkillsDir", str(layout.skills_dir)]

    result = subprocess.run(
        command,
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        timeout=HANG_GUARD_SECONDS,
        check=False,
        env=_environment(layout.dotnet_root),
    )

    assert result.returncode == 1, result.stdout + result.stderr
    assert not layout.install_dir.exists()


@pytest.mark.parametrize("shell", SHELLS)
def test_a_missing_runtime_without_a_console_stops_naming_install_dotnet(tmp_path: Path, layout: Layout, shell: str) -> None:
    layout = _without_runtime(tmp_path, layout)

    result = _run(shell, layout)

    assert result.returncode != 0
    output = _output(result)
    assert "the .NET 10 runtime (Microsoft.NETCore.App 10.*) is missing" in output
    assert "-InstallDotNet" in output
    assert "https://dotnet.microsoft.com/download/dotnet/10.0" in output
    assert not layout.install_dir.exists()


def test_a_folder_that_is_not_an_install_is_refused_and_left_alone(layout: Layout) -> None:
    layout.install_dir.mkdir()
    (layout.install_dir / "notes.txt").write_text("mine", encoding="utf-8")

    result = _run("pwsh", layout)

    assert result.returncode != 0
    assert "is not a godot-mcp install; it is left alone" in _output(result)
    assert sorted(path.name for path in layout.install_dir.iterdir()) == ["notes.txt"]


def test_a_file_that_is_not_a_zip_is_refused(tmp_path: Path, layout: Layout) -> None:
    broken = tmp_path / "broken.zip"
    broken.write_bytes(b"not a zip at all")

    result = _run("pwsh", layout, zip_path=broken)

    assert result.returncode != 0
    assert f"install: {broken} could not be unpacked" in _output(result)
    assert not layout.install_dir.exists()


@pytest.mark.parametrize("entry", ["skill/SKILL.md", "agent-sweep-skill/SKILL.md", "capture/godot-mcp-capture.exe"])
def test_a_zip_without_a_release_entry_is_refused(tmp_path: Path, stand_in: Path, layout: Layout, entry: str) -> None:
    entries = _release_entries(stand_in)
    del entries[entry]
    partial = _write_zip(tmp_path / "partial.zip", entries)

    result = _run("pwsh", layout, zip_path=partial)

    assert result.returncode != 0
    assert f"is not a godot-mcp release: it has no {entry}" in _output(result)
    assert not layout.install_dir.exists()


def test_package_refuses_a_publish_without_the_capture_helper(tmp_path: Path) -> None:
    root = tmp_path / "package-root"
    _publish_layout(root, capture_helper=False)

    result = _package(root, tmp_path / "package")

    assert result.returncode != 0
    assert "package: bin/publish/capture/godot-mcp-capture.exe is missing" in _output(result)
    assert not (tmp_path / "package").exists()


def test_package_refuses_a_publish_without_the_launcher(tmp_path: Path) -> None:
    root = tmp_path / "package-root"
    _publish_layout(root)
    (root / "bin" / "publish" / "launcher" / "godot-mcp.exe").unlink()

    result = _package(root, tmp_path / "package")

    assert result.returncode != 0
    assert "package: bin/publish/launcher/godot-mcp.exe is missing" in _output(result)
    assert not (tmp_path / "package").exists()


def test_a_missing_zip_path_is_refused(tmp_path: Path, layout: Layout) -> None:
    result = _run("pwsh", layout, zip_path=tmp_path / "nowhere.zip")

    assert result.returncode != 0
    assert f"install: -ZipPath {tmp_path / 'nowhere.zip'} does not exist." in _output(result)


def test_a_version_that_is_not_x_y_z_is_refused_before_any_download(layout: Layout) -> None:
    arguments = ["-Version", "latest", "-InstallDir", _quote(layout.install_dir), "-SkillsDir", _quote(layout.skills_dir)]

    result = _invoke("pwsh", _quote(SCRIPT_PATH), arguments, _environment(layout.dotnet_root))

    assert result.returncode != 0
    assert not layout.install_dir.exists()
    assert "-Version 'latest' is not a version; give one as X.Y.Z" in _output(result)
