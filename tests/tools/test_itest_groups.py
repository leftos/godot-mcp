"""Tests for run.ps1 itest-groups: the itest groups the changes since a ref touch, run in a temporary git repo holding a copy of run.ps1."""

from __future__ import annotations

import re
import shutil
import subprocess
from pathlib import Path

import pytest

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"
ALL_GROUPS = [
    "lifecycle",
    "stopping",
    "watchdog",
    "sessions",
    "attach",
    "input",
    "reads",
    "time",
    "prep",
    "recording",
    "capture",
    "headless",
    "scene",
    "nodes",
    "csharp",
    "scratch",
    "scratchsteps",
    "scratchpace",
    "scratchfolder",
    "scratchparallel",
    "scratchuntil",
    "scratchbackground",
]
# Every group a rule's 'all' selects: all of them but the visible-desktop ones.
ALL_BUT_VISIBLE = [group for group in ALL_GROUPS if group != "capture"]
# The groups that play scratch scenes, which a change to the scratch runner or the runtime tools it drives selects.
SCRATCH_GROUPS = ["scratch", "scratchsteps", "scratchpace", "scratchfolder", "scratchparallel", "scratchuntil", "scratchbackground"]
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
    assert _selected(_itest_groups(repo)) == ["time", "csharp", *SCRATCH_GROUPS]


def test_a_scratch_tools_part_selects_the_scratch_groups_and_csharp(repo: Path) -> None:
    _change(repo, "src/GodotMcp.Server/Tools/ScratchTools.cs")
    assert _selected(_itest_groups(repo)) == ["csharp", *SCRATCH_GROUPS]


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


@pytest.mark.parametrize(
    "changed",
    [
        "bridge/godot_mcp_input.gd",
        "src/GodotMcp.Server/Session/SessionRegistry.cs",
    ],
)
def test_a_shared_file_selects_every_group_but_the_visible_ones(repo: Path, changed: str) -> None:
    _change(repo, changed)
    assert _selected(_itest_groups(repo)) == ALL_BUT_VISIBLE


def test_a_change_to_run_ps1_selects_every_group_but_the_visible_ones(repo: Path) -> None:
    with (repo / "run.ps1").open("a", encoding="utf-8", newline="\n") as script:
        script.write("# changed\n")
    assert _selected(_itest_groups(repo)) == ALL_BUT_VISIBLE


@pytest.mark.parametrize(
    ("changed", "groups"),
    [
        ("src/GodotMcp.Server/Tools/RuntimeTools.Record.cs", ["recording", "capture"]),
        ("src/GodotMcp.Server/Session/RealtimeRecording.cs", ["capture"]),
        ("src/GodotMcp.Capture/Program.cs", ["capture"]),
        ("bridge/godot_mcp_window.gd", ALL_GROUPS),
        ("tools/hidden-desktop.ps1", ALL_GROUPS),
        ("src/GodotMcp.Server/Session/Installation.cs", ALL_GROUPS),
        ("tests/GodotMcp.IntegrationTests/Fixtures/SessionHarness.cs", ALL_GROUPS),
        ("tests/GodotMcp.TestSupport/RepoPaths.cs", ALL_GROUPS),
        ("tests/GodotMcp.IntegrationTests/CaptureHelperTests.cs", ["capture"]),
    ],
)
def test_a_change_to_recording_or_capture_selects_the_capture_group(repo: Path, changed: str, groups: list[str]) -> None:
    _change(repo, changed)
    assert _selected(_itest_groups(repo)) == groups


def test_an_untracked_test_class_file_selects_the_group_listing_it(repo: Path) -> None:
    _change(repo, "tests/GodotMcp.IntegrationTests/InputTests.cs")
    result = _itest_groups(repo)
    assert _selected(result) == ["input"]
    assert "itest-groups: input (from tests/GodotMcp.IntegrationTests/InputTests.cs)" in result.stdout


def test_a_change_to_the_parallel_scratch_folder_tests_selects_its_own_group(repo: Path) -> None:
    _change(repo, "tests/GodotMcp.IntegrationTests/ScratchFolderParallelTests.cs")
    assert _selected(_itest_groups(repo)) == ["scratchparallel"]


@pytest.mark.parametrize(
    ("changed", "groups"),
    [
        ("tests/GodotMcp.IntegrationTests/ScratchRunBase.cs", ["scratch", "scratchsteps", "scratchpace"]),
        ("tests/GodotMcp.IntegrationTests/ScratchRunnerTests.cs", SCRATCH_GROUPS),
        ("tests/GodotMcp.IntegrationTests/ScratchStepsRunTests.cs", ["scratchsteps"]),
        ("tests/GodotMcp.IntegrationTests/ScratchPaceRunTests.cs", ["scratchpace"]),
    ],
)
def test_the_split_scratch_run_classes_select_their_own_groups(repo: Path, changed: str, groups: list[str]) -> None:
    _change(repo, changed)
    assert _selected(_itest_groups(repo)) == groups


@pytest.mark.parametrize(
    ("changed", "groups"),
    [
        ("tests/GodotMcp.IntegrationTests/SessionStopTests.cs", ["stopping"]),
        ("tests/GodotMcp.IntegrationTests/WatchdogTests.cs", ["watchdog"]),
        ("tests/GodotMcp.IntegrationTests/ArmTests.cs", ["attach"]),
        ("src/GodotMcp.Server/Tools/ProjectTools.cs", ["lifecycle", "stopping", "watchdog", "sessions", "attach", "prep"]),
    ],
)
def test_the_split_lifecycle_and_sessions_classes_select_their_own_groups(repo: Path, changed: str, groups: list[str]) -> None:
    _change(repo, changed)
    assert _selected(_itest_groups(repo)) == groups


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


# A stand-in for the repo's gate wrapper: it writes the words it was called with, one per line, to .tmp/gate-args.txt
# and exits 7. The words come from the process argv after the -File script, since PowerShell's binder drops a bare --
# from $args.
GATE_STUB = """$argv = [Environment]::GetCommandLineArgs()
$at = [Array]::IndexOf($argv, '-File')
$words = @($argv | Select-Object -Skip ($at + 2))
$tmp = Join-Path (Split-Path -Parent $PSScriptRoot) '.tmp'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
Set-Content -LiteralPath (Join-Path $tmp 'gate-args.txt') -Value $words -Encoding utf8
exit 7
"""
INTEGRATION_TESTS = RUN_PS1.parent / "tests" / "GodotMcp.IntegrationTests"
TEST_CLASS = re.compile(r"^\s*public\s+sealed\s+(?:partial\s+)?class\s+(\w+Tests)\b", re.MULTILINE)


def _declared_test_classes() -> list[str]:
    sources = [source.read_text(encoding="utf-8") for source in INTEGRATION_TESTS.glob("*.cs")]
    return sorted({match.group(1) for source in sources for match in TEST_CLASS.finditer(source)})


@pytest.fixture
def routed_repo(repo: Path) -> Path:
    """The repo with the gate stub and a declaration of every integration test class, so itest's drift check passes."""
    (repo / "tools" / "gate.ps1").write_text(GATE_STUB, encoding="utf-8", newline="\n")
    declarations = "".join(f"public sealed class {name} {{ }}\n" for name in _declared_test_classes())
    classes = repo / "tests" / "GodotMcp.IntegrationTests" / "Classes.cs"
    classes.parent.mkdir(parents=True, exist_ok=True)
    classes.write_text(declarations, encoding="utf-8", newline="\n")
    _git(repo, "add", "--all")
    _git(repo, "commit", "--quiet", "-m", "gate stub and test classes")
    return repo


def _gate_args(repo: Path) -> Path:
    return repo / ".tmp" / "gate-args.txt"


def _gate_call(repo: Path, *selection: str) -> list[str]:
    log = str((repo / ".tmp" / "itest-gate.log").resolve())
    head = ["run", "--cd", str(repo.resolve()), "--log", log, "--timeout", "1800", "--slot", "heavy", "--tail", "15", "--"]
    return [*head, "pwsh", "-NoProfile", "-File", "run.ps1", "itest", "-InGate", *selection]


def _recorded_gate_call(repo: Path) -> list[str]:
    words = _gate_args(repo).read_text(encoding="utf-8-sig").splitlines()
    for at in (2, 4):
        words[at] = str(Path(words[at]).resolve())
    return words


def test_an_itest_goes_out_as_one_heavy_gate_run(routed_repo: Path) -> None:
    _change(routed_repo, "src/GodotMcp.Server/Tools/RuntimeTools.Time.cs")

    result = _itest(routed_repo, "-Since", "HEAD")

    assert result.returncode == 7, result.stdout + result.stderr
    assert "itest-groups: time (from src/GodotMcp.Server/Tools/RuntimeTools.Time.cs)" in result.stdout
    assert _recorded_gate_call(routed_repo) == _gate_call(routed_repo, "-Groups", ",".join(["time", "csharp", *SCRATCH_GROUPS]))


def test_an_itest_with_no_group_touched_makes_no_gate_call(routed_repo: Path) -> None:
    _change(routed_repo, "docs/x.md")

    result = _itest(routed_repo, "-Since", "HEAD")

    assert result.returncode == 0, result.stdout + result.stderr
    assert "itest: no group touched by the changes since HEAD" in result.stdout
    assert not _gate_args(routed_repo).exists()


def test_an_unknown_group_is_refused(routed_repo: Path) -> None:
    result = _itest(routed_repo, "-InGate", "-Groups", "time,nosuch")

    assert result.returncode == 2, result.stdout + result.stderr
    assert f"itest -Groups names no group nosuch; the groups are {', '.join(ALL_GROUPS)}." in result.stderr
    assert not _gate_args(routed_repo).exists()


def test_groups_with_since_is_refused(routed_repo: Path) -> None:
    result = _itest(routed_repo, "-Groups", "time", "-Since", "HEAD")

    assert result.returncode == 2, result.stdout + result.stderr
    assert "itest takes one of -Filter, -Since or -Groups." in result.stderr
    assert not _gate_args(routed_repo).exists()


def test_an_unknown_since_ref_is_refused_before_the_gate(routed_repo: Path) -> None:
    result = _itest(routed_repo, "-Since", "no-such-ref")

    assert result.returncode == 2, result.stdout + result.stderr
    assert "no-such-ref" in result.stderr
    assert not _gate_args(routed_repo).exists()


def test_a_filtered_itest_passes_its_filter_through(routed_repo: Path) -> None:
    result = _itest(routed_repo, "-Filter", "*SessionStopTests")

    assert result.returncode == 7, result.stdout + result.stderr
    assert _recorded_gate_call(routed_repo) == _gate_call(routed_repo, "-Filter", "*SessionStopTests")


def test_an_itest_in_its_gate_does_not_call_the_gate_again(routed_repo: Path) -> None:
    _change(routed_repo, "docs/x.md")
    _git(routed_repo, "commit", "--quiet", "--all", "-m", "docs")

    result = _itest(routed_repo, "-InGate", "-Since", "HEAD~1")

    assert result.returncode == 0, result.stdout + result.stderr
    assert not _gate_args(routed_repo).exists()


def test_filter_with_since_is_refused_before_the_gate(routed_repo: Path) -> None:
    result = _itest(routed_repo, "-Filter", "x", "-Since", "HEAD")

    assert result.returncode == 2, result.stdout + result.stderr
    assert not _gate_args(routed_repo).exists()
