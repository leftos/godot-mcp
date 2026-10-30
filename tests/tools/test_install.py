"""Tests for tools/install.ps1 and tools/InstallLayout.psm1: the versioned layout, the skill junctions and the agent
sweep, run against temporary folders."""

from __future__ import annotations

import ctypes
import os
import shutil
import subprocess
import sys
import threading
from collections import Counter
from collections.abc import Iterator
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path

import pytest
from conftest import SHELLS
from fake_installed_server import PROJECT, SESSION_ID, FakeServer, child_pid, start_fake_server, start_process, write_transcript

TOOLS = Path(__file__).resolve().parents[2] / "tools"
SCRIPT_PATH = TOOLS / "install.ps1"
LAYOUT_MODULE = TOOLS / "InstallLayout.psm1"
# Any PE file with a version resource stands in for the published server dll, whose ProductVersion install.ps1 records.
VERSIONED_DLL = Path(os.environ.get("SYSTEMROOT", r"C:\Windows")) / "System32" / "kernel32.dll"
LAUNCHER_TEXT = "launcher v1"
# Hang guards only: a passing run never comes near them.
HANG_GUARD_SECONDS = 120

pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="junctions and the installers are Windows-only")


@dataclass(frozen=True)
class Layout:
    root: Path
    install_dir: Path
    skills_dir: Path

    @property
    def publish(self) -> Path:
        return self.root / "bin" / "publish"

    @property
    def publish_dll(self) -> Path:
        return self.publish / "godot-mcp.dll"

    @property
    def launcher(self) -> Path:
        return self.install_dir / "godot-mcp.exe"

    @property
    def versions(self) -> Path:
        return self.install_dir / "versions"

    @property
    def link(self) -> Path:
        return self.skills_dir / "godot-mcp"

    @property
    def sweep_link(self) -> Path:
        return self.skills_dir / "godot-agent-sweep"

    @property
    def checkout_skill(self) -> Path:
        """The checkout's own skills/godot-mcp: what an install made before the skills shipped with the build linked."""
        return self.root / "skills" / "godot-mcp"

    def current(self) -> str:
        return (self.install_dir / "current").read_text(encoding="utf-8").splitlines()[0]

    def version_folders(self) -> list[str]:
        return sorted(path.name for path in self.versions.iterdir())


def _layout(tmp_path: Path, stand_in: Path, *, with_skill: bool = True, with_sweep_skill: bool = True) -> Layout:
    layout = Layout(root=tmp_path / "repo", install_dir=tmp_path / "installed", skills_dir=tmp_path / "home" / "skills")
    publish = layout.publish
    (publish / "bridge").mkdir(parents=True)
    (publish / "bridge" / "marker.txt").write_text("bridge\n", encoding="utf-8")
    (publish / "launcher").mkdir()
    (publish / "launcher" / "godot-mcp.exe").write_text(LAUNCHER_TEXT, encoding="utf-8")
    shutil.copyfile(stand_in, publish / "godot-mcp.exe")
    shutil.copyfile(VERSIONED_DLL, layout.publish_dll)
    # A publish copies the checkout's skills into bin/publish; the install copies them on into the version folder.
    if with_skill:
        (publish / "skill").mkdir()
        (publish / "skill" / "SKILL.md").write_text("# godot-mcp\n", encoding="utf-8")
    if with_sweep_skill:
        (publish / "agent-sweep-skill").mkdir()
        (publish / "agent-sweep-skill" / "SKILL.md").write_text("# godot-agent-sweep\n", encoding="utf-8")
    for name in ("godot-mcp", "godot-agent-sweep"):
        (layout.root / "skills" / name).mkdir(parents=True)
        (layout.root / "skills" / name / "SKILL.md").write_text(f"# {name} in the checkout\n", encoding="utf-8")
    layout.skills_dir.mkdir(parents=True)
    return layout


@pytest.fixture
def layout(tmp_path: Path, stand_in: Path) -> Layout:
    return _layout(tmp_path, stand_in)


def _install(layout: Layout, *, home: Path | None = None, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    """Runs the install; with a home, USERPROFILE points there, where install looks for Claude Code's transcripts."""
    environment = dict(os.environ)
    if home is not None:
        environment["USERPROFILE"] = str(home)
    environment.update(env or {})
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
    return subprocess.run(command, capture_output=True, text=True, timeout=HANG_GUARD_SECONDS, check=False, env=environment)


def _installed(layout: Layout, *, home: Path | None = None) -> subprocess.CompletedProcess[str]:
    result = _install(layout, home=home)
    assert result.returncode == 0, _output(result)
    return result


def _output(result: subprocess.CompletedProcess[str]) -> str:
    return result.stdout + result.stderr


def _product_version(path: Path) -> str:
    command = ["pwsh", "-NoProfile", "-Command", f"(Get-Item -LiteralPath '{path}').VersionInfo.ProductVersion"]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=True)
    return result.stdout.strip()


def _folder_name(layout: Layout) -> str:
    return _product_version(layout.publish_dll).replace("+", "-")


def _make_junction(link: Path, target: Path) -> None:
    subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)], capture_output=True, check=True)


def _points_at(link: Path, target: Path) -> bool:
    return os.path.isjunction(link) and os.path.realpath(link).lower() == os.path.realpath(target).lower()


def _tree(folder: Path) -> dict[str, bytes]:
    return {str(path.relative_to(folder)): path.read_bytes() for path in folder.rglob("*") if path.is_file()}


@contextmanager
def _running(exe: Path, flag: Path, *arguments: str, session_id: str | None = SESSION_ID) -> Iterator[FakeServer]:
    """Runs the stand-in exe until the block ends, by the flag file its last argument names."""
    flag.write_text("run", encoding="utf-8")
    server = start_process(exe, [*arguments, str(flag)], session_id=session_id)
    try:
        yield server
    finally:
        flag.unlink(missing_ok=True)
        server.parent.wait(timeout=HANG_GUARD_SECONDS)
        server.stop()


def test_a_fresh_install_lays_out_the_launcher_the_pointer_and_a_version_folder(layout: Layout) -> None:
    result = _installed(layout)

    name = _folder_name(layout)
    version = _product_version(layout.publish_dll)
    folder = layout.versions / name
    assert layout.current() == name
    assert layout.launcher.read_text(encoding="utf-8") == LAUNCHER_TEXT
    assert (folder / "godot-mcp.exe").read_bytes() == (layout.publish / "godot-mcp.exe").read_bytes()
    assert (folder / "bridge" / "marker.txt").is_file()
    assert not (folder / "launcher").exists()
    assert sorted(path.name for path in layout.install_dir.iterdir()) == ["VERSION", "current", "godot-mcp.exe", "install.lock", "versions"]
    assert _points_at(layout.link, folder / "skill")
    assert _points_at(layout.sweep_link, folder / "agent-sweep-skill")
    expected = f"install: server at {layout.launcher} (version {version}), skill linked at {layout.link}"
    assert expected in result.stdout
    assert f"install: agent sweep skill linked at {layout.sweep_link}" in result.stdout
    assert "stand-in: swept the agent files" in result.stdout


def test_install_fails_when_the_sweep_exits_non_zero_but_undoes_nothing(layout: Layout) -> None:
    result = _install(layout, env={"STANDIN_SWEEP_STATUS": "3"})

    assert result.returncode == 1
    assert "install: the agent sweep exited with status 3" in _output(result)
    folder = layout.versions / _folder_name(layout)
    assert layout.current() == folder.name
    assert _points_at(layout.link, folder / "skill")
    assert _points_at(layout.sweep_link, folder / "agent-sweep-skill")


def test_install_writes_the_version_file_beside_the_launcher_and_in_the_version_folder(layout: Layout) -> None:
    # In System32 the dll's version comes from its .mui file and a copy's does not, so read the copy install reads.
    version = _product_version(layout.publish_dll)
    assert version

    _installed(layout)
    _installed(layout)

    assert (layout.install_dir / "VERSION").read_bytes() == f"{version}\n".encode()
    assert (layout.versions / layout.current() / "VERSION").read_bytes() == f"{version}\n".encode()
    assert not (layout.publish / "VERSION").exists()


def test_each_install_of_one_build_gets_the_next_suffix_and_keeps_the_previous_version(layout: Layout) -> None:
    name = _folder_name(layout)

    _installed(layout)
    _installed(layout)
    assert layout.version_folders() == [name, f"{name}.2"]
    assert layout.current() == f"{name}.2"

    _installed(layout)
    assert layout.version_folders() == [f"{name}.2", f"{name}.3"]
    assert layout.current() == f"{name}.3"
    assert _points_at(layout.link, layout.versions / f"{name}.3" / "skill")


def test_install_links_the_skills_from_the_version_folder_not_the_checkout(layout: Layout) -> None:
    _installed(layout)

    folder = layout.versions / layout.current()
    assert _points_at(layout.link, folder / "skill")
    # The checkout's own skills are not the target, so a skill edited there reaches no installed agent.
    assert not _points_at(layout.link, layout.checkout_skill)
    assert (layout.link / "SKILL.md").read_text(encoding="utf-8") == "# godot-mcp\n"


def test_install_replaces_the_checkout_skill_junction_with_one_into_the_version_folder(layout: Layout) -> None:
    # The layout every install made before the skills shipped with the build left: a junction into the checkout.
    _make_junction(layout.link, layout.checkout_skill)
    assert _points_at(layout.link, layout.checkout_skill)

    _installed(layout)

    assert _points_at(layout.link, layout.versions / layout.current() / "skill")
    assert (layout.checkout_skill / "SKILL.md").read_text(encoding="utf-8") == "# godot-mcp in the checkout\n"


def test_a_published_skill_edit_reaches_the_junction_only_through_the_next_install(layout: Layout) -> None:
    _installed(layout)
    (layout.checkout_skill / "SKILL.md").write_text("# edited in the checkout\n", encoding="utf-8")
    (layout.publish / "skill" / "SKILL.md").write_text("# published\n", encoding="utf-8")

    assert (layout.link / "SKILL.md").read_text(encoding="utf-8") == "# godot-mcp\n"

    _installed(layout)

    assert (layout.link / "SKILL.md").read_text(encoding="utf-8") == "# published\n"


def test_install_replaces_a_junction_pointing_elsewhere(tmp_path: Path, layout: Layout) -> None:
    elsewhere = tmp_path / "old-worktree" / "skills" / "godot-mcp"
    elsewhere.mkdir(parents=True)
    (elsewhere / "SKILL.md").write_text("# old\n", encoding="utf-8")
    _make_junction(layout.link, elsewhere)

    _installed(layout)

    assert _points_at(layout.link, layout.versions / layout.current() / "skill")
    assert (elsewhere / "SKILL.md").read_text(encoding="utf-8") == "# old\n"


def test_install_refuses_a_real_folder_at_the_link(layout: Layout) -> None:
    layout.link.mkdir()
    (layout.link / "notes.md").write_text("mine\n", encoding="utf-8")

    result = _install(layout)

    assert result.returncode == 1
    assert f"install: {layout.link} exists and is not a junction; move it away and run install again." in _output(result)
    assert not os.path.isjunction(layout.link)
    assert (layout.link / "notes.md").read_text(encoding="utf-8") == "mine\n"


def test_install_fails_without_the_published_dll(layout: Layout) -> None:
    layout.publish_dll.unlink()

    result = _install(layout)

    assert result.returncode == 1
    assert "install: bin/publish/godot-mcp.dll is missing; run publish first." in _output(result)
    assert not layout.install_dir.exists()
    assert not layout.link.exists()


def test_install_fails_without_the_published_launcher(layout: Layout) -> None:
    (layout.publish / "launcher" / "godot-mcp.exe").unlink()

    result = _install(layout)

    assert result.returncode == 1
    assert "install: bin/publish/launcher/godot-mcp.exe is missing; run publish first." in _output(result)
    assert not layout.install_dir.exists()


def test_install_fails_when_the_dll_carries_no_product_version(layout: Layout) -> None:
    # A file that is not a PE has no version resource, so its ProductVersion is empty.
    layout.publish_dll.write_text("not really a dll", encoding="utf-8")
    assert not _product_version(layout.publish_dll)
    layout.install_dir.mkdir()
    (layout.install_dir / "godot-mcp.exe").write_text("the old exe", encoding="utf-8")
    (layout.install_dir / "VERSION").write_text("0.1.0+abcdef0\n", encoding="utf-8")

    result = _install(layout)

    assert result.returncode == 1
    assert "install: bin/publish/godot-mcp.dll carries no product version; run publish again." in _output(result)
    assert sorted(path.name for path in layout.install_dir.iterdir()) == ["VERSION", "godot-mcp.exe"]
    assert (layout.install_dir / "godot-mcp.exe").read_text(encoding="utf-8") == "the old exe"
    assert not layout.link.exists()


def test_a_version_that_fails_its_start_check_is_never_named(layout: Layout) -> None:
    _installed(layout)
    first = layout.current()
    # where.exe refuses --list-tools with a non-zero status, standing in for a server that cannot start.
    shutil.copyfile(Path(os.environ.get("SYSTEMROOT", r"C:\Windows")) / "System32" / "where.exe", layout.publish / "godot-mcp.exe")

    result = _install(layout)

    assert result.returncode == 1
    assert "--list-tools exited with status" in _output(result)
    assert layout.current() == first
    assert layout.version_folders() == [first]


def _flat_install(layout: Layout) -> None:
    """Today's flat folder: the server's files straight in the install folder, no pointer."""
    layout.install_dir.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(VERSIONED_DLL, layout.install_dir / "godot-mcp.dll")
    (layout.install_dir / "bridge").mkdir()
    (layout.install_dir / "bridge" / "marker.txt").write_text("flat\n", encoding="utf-8")
    (layout.install_dir / "VERSION").write_text("0.1.0+abcdef0\n", encoding="utf-8")


def test_a_flat_install_with_a_running_server_is_stopped_named_and_migrated(tmp_path: Path, layout: Layout) -> None:
    home = tmp_path / "userprofile"
    write_transcript(home, SESSION_ID)
    _flat_install(layout)
    server = start_fake_server(layout.install_dir, session_id=SESSION_ID)
    try:
        result = _installed(layout, home=home)

        assert not server.is_running()
        expected = (
            f"install: stopped the godot-mcp server (pid {server.pid}) of Claude session {SESSION_ID} in {PROJECT}; run /mcp there to reconnect it"
        )
        assert expected in result.stdout
        assert sorted(path.name for path in layout.install_dir.iterdir()) == ["VERSION", "current", "godot-mcp.exe", "install.lock", "versions"]
        assert layout.launcher.read_text(encoding="utf-8") == LAUNCHER_TEXT
        assert layout.version_folders() == [_folder_name(layout)]
    finally:
        server.stop()


def test_install_stops_a_flat_server_of_an_unknown_client(tmp_path: Path, layout: Layout) -> None:
    _flat_install(layout)
    server = start_fake_server(layout.install_dir, session_id=None)
    try:
        result = _installed(layout, home=tmp_path / "userprofile")

        assert not server.is_running()
        expected = f"install: stopped the godot-mcp server (pid {server.pid}) of an unknown client; run /mcp there to reconnect it"
        assert expected in result.stdout
    finally:
        server.stop()


def test_a_server_running_from_a_version_keeps_it_until_it_stops(tmp_path: Path, layout: Layout) -> None:
    home = tmp_path / "userprofile"
    write_transcript(home, SESSION_ID)
    _installed(layout)
    first = layout.versions / layout.current()
    before = _tree(first)
    version = _product_version(layout.publish_dll)

    with _running(first / "godot-mcp.exe", tmp_path / "server.flag") as server:
        second = _installed(layout, home=home)
        assert server.is_running()
        assert layout.current() == f"{first.name}.2"
        assert _tree(first) == before
        assert f"install: Claude session {SESSION_ID} ({PROJECT}) still runs {version}; /mcp it to use {version}" in second.stdout

        # Neither current nor previous now, but still running: its folder is kept whole.
        _installed(layout, home=home)
        assert server.is_running()
        assert _tree(first) == before

    _installed(layout)
    assert first.name not in layout.version_folders()
    assert layout.version_folders() == [f"{first.name}.3", f"{first.name}.4"]


def test_a_file_held_open_in_an_old_version_keeps_its_folder_whole(layout: Layout) -> None:
    _installed(layout)
    first = layout.versions / layout.current()
    before = _tree(first)

    with open(first / "bridge" / "marker.txt", "rb"):
        _installed(layout)
        _installed(layout)
        assert _tree(first) == before

    _installed(layout)
    assert first.name not in layout.version_folders()
    assert not [name for name in layout.version_folders() if name.startswith(".trash-")]


def test_a_held_install_lock_waits_then_refuses(layout: Layout) -> None:
    layout.install_dir.mkdir()
    lock = layout.install_dir / "install.lock"
    with open(lock, "a"):
        result = _install(layout, env={"GODOT_MCP_INSTALL_LOCK_WAIT_S": "1"})

    assert result.returncode == 1
    assert f"install: another install holds {lock}; try again once it ends" in _output(result)
    assert not (layout.install_dir / "current").exists()
    assert not layout.versions.exists()


def test_a_running_launcher_is_renamed_aside_replaced_and_removed_once_it_stops(tmp_path: Path, stand_in: Path, layout: Layout) -> None:
    home = tmp_path / "userprofile"
    write_transcript(home, SESSION_ID)
    _installed(layout)
    first = layout.versions / layout.current()
    version = _product_version(layout.publish_dll)
    # A launcher stand-in, started by a Claude session, starts the current version's server.
    shutil.copyfile(stand_in, layout.launcher)

    with _running(layout.launcher, tmp_path / "server.flag", "--start", str(first / "godot-mcp.exe")) as launcher:
        child_pid(launcher.pid)
        result = _installed(layout, home=home)

        assert launcher.is_running()
        assert layout.launcher.read_text(encoding="utf-8") == LAUNCHER_TEXT
        assert len(list(layout.install_dir.glob("godot-mcp.exe.old-*"))) == 1
        assert f"install: Claude session {SESSION_ID} ({PROJECT}) still runs {version}; /mcp it to use {version}" in result.stdout

    _installed(layout)
    assert not list(layout.install_dir.glob("godot-mcp.exe.old-*"))
    assert layout.launcher.read_text(encoding="utf-8") == LAUNCHER_TEXT


def test_install_fails_without_the_skill(tmp_path: Path, stand_in: Path) -> None:
    layout = _layout(tmp_path, stand_in, with_skill=False)

    result = _install(layout)

    assert result.returncode == 1
    assert f"install: {layout.publish / 'skill' / 'SKILL.md'} is missing; run publish first." in _output(result)
    assert not layout.link.exists()
    assert not layout.install_dir.exists()


def test_install_fails_without_the_agent_sweep_skill(tmp_path: Path, stand_in: Path) -> None:
    layout = _layout(tmp_path, stand_in, with_sweep_skill=False)

    result = _install(layout)

    assert result.returncode == 1
    assert f"install: {layout.publish / 'agent-sweep-skill' / 'SKILL.md'} is missing; run publish first." in _output(result)
    assert not layout.sweep_link.exists()
    assert not layout.install_dir.exists()


GENERIC_READ = 0x80000000
SHARE_ALL = 0x7
OPEN_EXISTING = 3
INVALID_HANDLE = ctypes.c_void_p(-1).value


def _read_pointer_until(path: Path, done: threading.Event, counts: Counter[str]) -> None:
    """Opens the pointer as the launcher does (sharing read, write and delete) and reads it, until done is set."""
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateFileW.restype = ctypes.c_void_p
    kernel32.CreateFileW.argtypes = [
        ctypes.c_wchar_p,
        ctypes.c_uint32,
        ctypes.c_uint32,
        ctypes.c_void_p,
        ctypes.c_uint32,
        ctypes.c_uint32,
        ctypes.c_void_p,
    ]
    kernel32.ReadFile.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32), ctypes.c_void_p]
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    while not done.is_set():
        handle = kernel32.CreateFileW(str(path), GENERIC_READ, SHARE_ALL, None, OPEN_EXISTING, 0, None)
        if handle == INVALID_HANDLE:
            counts[f"open failed with error {ctypes.get_last_error()}"] += 1
            continue
        buffer = ctypes.create_string_buffer(64)
        read = ctypes.c_uint32()
        kernel32.ReadFile(handle, buffer, 64, ctypes.byref(read), None)
        kernel32.CloseHandle(handle)
        text = buffer.raw[: read.value]
        counts["whole" if text in (b"A\n", b"B\n") else f"partial {text!r}"] += 1


@pytest.mark.parametrize("shell", SHELLS)
def test_a_pointer_replaced_under_a_reader_is_never_missing_or_partial(tmp_path: Path, shell: str) -> None:
    install_dir = tmp_path / "installed"
    install_dir.mkdir()
    (install_dir / "current").write_bytes(b"A\n")
    counts: Counter[str] = Counter()
    done = threading.Event()
    reader = threading.Thread(target=_read_pointer_until, args=(install_dir / "current", done, counts))
    source = (
        f"Import-Module -Name '{LAYOUT_MODULE}'; "
        f"foreach ($i in 1..300) {{ Set-CurrentVersion -InstallDir '{install_dir}' -Name @('A', 'B')[$i % 2] }}"
    )

    reader.start()
    try:
        writer = subprocess.run(
            [shell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", source],
            capture_output=True,
            text=True,
            timeout=HANG_GUARD_SECONDS,
            check=False,
        )
    finally:
        done.set()
        reader.join(timeout=HANG_GUARD_SECONDS)

    assert writer.returncode == 0, writer.stdout + writer.stderr
    assert counts["whole"] > 0
    assert set(counts) == {"whole"}, counts
    assert (install_dir / "current").read_bytes() == b"A\n"
    assert sorted(path.name for path in install_dir.iterdir()) == ["current"]
