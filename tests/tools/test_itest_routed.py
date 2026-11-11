"""Tests for run.ps1's routed itest: the gate runs it goes out as, its check that a run happened, the -InGate refusal
of a run that would take a visible-desktop group, and the desktops .gate/desktop.toml gives the two runs."""

from __future__ import annotations

import re
import subprocess
import tomllib
from pathlib import Path

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"
DESKTOP = Path(__file__).resolve().parents[2] / ".gate" / "desktop.toml"

# Loads named run.ps1 functions alone, without running the script.
LOADER = """$ast = [System.Management.Automation.Language.Parser]::ParseFile($RunPs1, [ref]$null, [ref]$null)
foreach ($name in $Names) {
    $function = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $function) {
        throw "run.ps1 has no function $name"
    }
    . ([scriptblock]::Create($function.Extent.Text))
}
"""

# Prints what Get-GateNoRunLine returns for a log, or <null>.
HELPER_DRIVER = (
    """param([string]$RunPs1, [string]$Log)
$ErrorActionPreference = 'Stop'
$Names = @('Get-GateNoRunLine')
"""
    + LOADER
    + """$line = Get-GateNoRunLine -Log $Log
if ($null -eq $line) { Write-Output '<null>' } else { Write-Output $line }
"""
)

# The groups, visible groups and namespace the routing tests run against, so that they never depend on the real
# tables' contents: three groups, the last of them the one visible-desktop group.
TABLES = """$itestGroups = [ordered]@{
    lifecycle = @('LifecycleTests')
    recording = @('RecordingTests')
    capture   = @('CaptureHelperTests', 'RealtimeRecordingTests')
}
$itestVisibleGroups = @('capture')
$itestNamespace = 'GodotMcp.IntegrationTests'
"""

# Runs Invoke-ItestRouted against a stub gate wrapper that logs, and exits with the function's status. -Filters and
# -GroupNames are comma-separated, as the gate's words would carry them.
ROUTED_DRIVER = (
    """param([string]$RunPs1, [string]$Root, [string]$Stub, [string]$Tables, [string]$Filters = '', [string]$GroupNames = '', [string]$Since = '')
$ErrorActionPreference = 'Stop'
$Names = @(
    'Get-GateKillLine', 'Get-GateKill', 'Get-GateNoRunLine', 'Test-ItestFilterIsVisible',
    'Invoke-ItestRoutedGate', 'Invoke-ItestRoutedFilter', 'Invoke-ItestRoutedRuns',
    'Invoke-ItestRoutedGroups', 'Invoke-ItestRouted'
)
"""
    + LOADER
    + """. $Tables
function Get-ItestRefusal { return '' }
function Invoke-ItestGroupSelection { param([string]$Since) return @{ Status = 0; Groups = @() } }
$root = $Root
$logDir = Join-Path $Root '.tmp'
$gateWrapper = $Stub
$pwshPath = [Environment]::ProcessPath
$itestRoutedCeiling = 1800
$itestCaptureCeiling = 420
$filterList = @()
if ($Filters) { $filterList = @($Filters -split ',') }
$groupList = @()
if ($GroupNames) { $groupList = @($GroupNames -split ',') }
exit (Invoke-ItestRouted -Filters $filterList -Since $Since -GroupNames $groupList)
"""
)

# Runs Invoke-Itest, the -InGate form, with any build it would run turned into a failure, so that a run that is not
# refused before its build fails the test.
REFUSAL_DRIVER = (
    """param([string]$RunPs1, [string]$Tables, [string]$Filters = '', [string]$GroupNames = '')
$ErrorActionPreference = 'Stop'
$Names = @(
    'Get-ItestRefusal', 'Get-ItestGroupsRefusal', 'Get-ItestFilterSpanRefusal', 'Get-ItestFilterVisibleGroups',
    'Test-ItestFilterIsHidden', 'Test-ItestFilterIsVisible', 'Test-ItestGroupsAreVisible', 'Get-ItestRunGroup',
    'Invoke-Itest'
)
"""
    + LOADER
    + """. $Tables
function Get-ItestGroupDrift { return '' }
function Invoke-ItestGroupSelection { param([string]$Since) return @{ Status = 0; Groups = @() } }
function Invoke-BuildForRefusalTest { throw 'itest built the test project on a run it should have refused' }
function Invoke-ItestFiltered { param([string[]]$Filters) return (Invoke-BuildForRefusalTest) }
function Invoke-ItestByGroup { param([string[]]$Groups) return (Invoke-BuildForRefusalTest) }
$filterList = @()
if ($Filters) { $filterList = @($Filters -split ',') }
$groupList = @()
if ($GroupNames) { $groupList = @($GroupNames -split ',') }
exit (Invoke-Itest -Filters $filterList -Since '' -GroupNames $groupList)
"""
)

# The stub gate wrapper: writes its log lines to the --log path, appends the words after `--` to the calls file, and
# exits with the status of that call's position in the statuses list, else 0.
STUB = """$log = $args[[array]::IndexOf($args, '--log') + 1]
Set-Content -LiteralPath $log -Value @(LINES) -Encoding utf8
$dash = [array]::IndexOf($args, '--')
$words = @($args[($dash + 1)..($args.Count - 1)])
$calls = CALLS
$prior = 0
if (Test-Path -LiteralPath $calls) { $prior = @(Get-Content -LiteralPath $calls).Count }
Add-Content -LiteralPath $calls -Value ($words -join ' ') -Encoding utf8
Write-Output 'stub gate ran'
$statuses = @(STATUSES)
if ($prior -lt $statuses.Count) { exit $statuses[$prior] }
exit 0
"""

STAMP = "2026-11-10T07:07:11.460395-08:00"
BUSY = "gate: not routed: the builder's desktop stayed busy for 1800 s"
FASTER = "gate: not routed: faster here"
PASSED = (
    "gate: passed in 895 s on builder@leftos-buildbox (load-adjusted 27 s, ceiling 1800 s; command 190 s, push 3 s, "
    r"outputs 0 s). Full output: X:\dev\godot-mcp\.tmp\itest-gate.log"
)
FAILED = r"gate: FAILED (status 1). Full output: X:\dev\godot-mcp\.tmp\itest-gate.log"
STALLED = (
    "gate: STALLED: no output and 0.25 s of CPU (under the 1.20 s floor) for 120 s not waiting for a processor "
    "(machine free 100% on average); killed pwsh -NoProfile -File run.ps1 itest -InGate"
)
NO_VERDICT = "the gate's log has no passed verdict"
VISIBLE_REFUSAL = "itest -InGate runs on a hidden desktop, where the capture group cannot run; run it as run.ps1 itest-capture."

HIDDEN_RUN = "pwsh -NoProfile -File run.ps1 itest -InGate -Groups lifecycle,recording"
CAPTURE_RUN = "pwsh -NoProfile -File run.ps1 itest-capture"


def _stamped(line: str) -> str:
    """A gate line as the gate writes it to its log: stamped with its time and the gat stream."""
    return f"{STAMP} gat {line}"


def _no_run_line(tmp_path: Path, log_lines: list[str]) -> str:
    driver = tmp_path / "driver.ps1"
    driver.write_text(HELPER_DRIVER, encoding="utf-8", newline="\n")
    log = tmp_path / "itest-gate.log"
    log.write_text("".join(f"{line}\n" for line in log_lines), encoding="utf-8", newline="\n")

    command = ["pwsh", "-NoProfile", "-File", str(driver), "-RunPs1", str(RUN_PS1), "-Log", str(log)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)

    assert result.returncode == 0, result.stderr
    assert result.stderr == ""
    return result.stdout.strip()


def _write_stub(tmp_path: Path, log_lines: list[str], statuses: list[int]) -> tuple[Path, Path]:
    """The stub gate wrapper and the file it appends each call's words to."""
    calls = tmp_path / "calls.txt"
    stub = tmp_path / "gate.ps1"
    quoted = ", ".join("'" + _stamped(line).replace("'", "''") + "'" for line in log_lines)
    stub.write_text(
        STUB.replace("LINES", quoted).replace("CALLS", f"'{calls}'").replace("STATUSES", ", ".join(str(status) for status in statuses)),
        encoding="utf-8",
        newline="\n",
    )
    return stub, calls


def _run(driver_source: str, tmp_path: Path, driver_args: dict[str, str]) -> subprocess.CompletedProcess[str]:
    driver = tmp_path / "driver.ps1"
    driver.write_text(driver_source, encoding="utf-8", newline="\n")
    command = ["pwsh", "-NoProfile", "-File", str(driver), "-RunPs1", str(RUN_PS1), "-Tables", str(tmp_path / "tables.ps1")]
    for flag, value in driver_args.items():
        if value:
            command += [flag, value]
    return subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)


def _run_routed(
    tmp_path: Path,
    *,
    statuses: list[int] | None = None,
    log_lines: list[str] | None = None,
    filters: str = "",
    group_names: str = "",
) -> tuple[subprocess.CompletedProcess[str], list[str]]:
    """Runs the routed itest against the stub gate; returns its result and every gate call's words, in order."""
    root = tmp_path / "repo"
    (root / ".tmp").mkdir(parents=True, exist_ok=True)
    (tmp_path / "tables.ps1").write_text(TABLES, encoding="utf-8", newline="\n")
    stub, calls = _write_stub(tmp_path, log_lines or [PASSED], statuses or [0])

    result = _run(
        ROUTED_DRIVER,
        tmp_path,
        {"-Root": str(root), "-Stub": str(stub), "-Filters": filters, "-GroupNames": group_names},
    )
    seen = calls.read_text(encoding="utf-8").splitlines() if calls.exists() else []
    return result, seen


def _run_refusal(tmp_path: Path, *, filters: str = "", group_names: str = "") -> subprocess.CompletedProcess[str]:
    """Runs the -InGate itest form, with a build it would run turned into a failure."""
    (tmp_path / "tables.ps1").write_text(TABLES, encoding="utf-8", newline="\n")
    return _run(REFUSAL_DRIVER, tmp_path, {"-Filters": filters, "-GroupNames": group_names})


def test_a_busy_desktop_with_no_verdict_names_the_not_routed_line(tmp_path: Path) -> None:
    lines = [_stamped("gate: routing to builder@leftos-buildbox"), _stamped(BUSY)]

    assert _no_run_line(tmp_path, lines) == BUSY


def test_a_passed_verdict_is_a_run(tmp_path: Path) -> None:
    lines = [_stamped("gate: running pwsh"), _stamped(PASSED)]

    assert _no_run_line(tmp_path, lines) == "<null>"


def test_a_not_routed_line_followed_by_a_passed_verdict_is_a_run(tmp_path: Path) -> None:
    lines = [_stamped(FASTER), _stamped("gate: running pwsh"), _stamped(PASSED)]

    assert _no_run_line(tmp_path, lines) == "<null>"


def test_a_not_routed_line_followed_by_a_failed_verdict_is_a_run(tmp_path: Path) -> None:
    lines = [_stamped(FASTER), _stamped("gate: running pwsh"), _stamped(FAILED)]

    assert _no_run_line(tmp_path, lines) == "<null>"


def test_a_not_routed_line_followed_by_a_kill_line_is_a_run(tmp_path: Path) -> None:
    lines = [_stamped(FASTER), _stamped("gate: running pwsh"), _stamped(STALLED)]

    assert _no_run_line(tmp_path, lines) == "<null>"


def test_an_empty_log_has_no_passed_verdict(tmp_path: Path) -> None:
    assert _no_run_line(tmp_path, []) == NO_VERDICT


def test_a_routed_itest_whose_gate_exits_0_without_a_verdict_returns_1(tmp_path: Path) -> None:
    result, calls = _run_routed(tmp_path, log_lines=[BUSY], group_names="lifecycle")

    assert result.returncode == 1, result.stdout + result.stderr
    assert len(calls) == 1, calls
    assert "stub gate ran" in result.stdout.splitlines()
    assert result.stdout.splitlines()[-1] == f"itest: no test ran: {BUSY}"


def test_a_bare_routed_itest_runs_the_hidden_groups_then_the_capture_group(tmp_path: Path) -> None:
    result, calls = _run_routed(tmp_path)

    assert result.returncode == 0, result.stdout + result.stderr
    assert calls == [HIDDEN_RUN, CAPTURE_RUN]


def test_a_routed_itest_of_hidden_groups_runs_one_gate_in_table_order(tmp_path: Path) -> None:
    result, calls = _run_routed(tmp_path, group_names="recording,lifecycle")

    assert result.returncode == 0, result.stdout + result.stderr
    assert calls == [HIDDEN_RUN]


def test_a_routed_itest_of_the_capture_group_runs_itest_capture_alone(tmp_path: Path) -> None:
    result, calls = _run_routed(tmp_path, group_names="capture")

    assert result.returncode == 0, result.stdout + result.stderr
    assert calls == [CAPTURE_RUN]


def test_a_routed_itest_filter_of_a_capture_class_runs_itest_capture(tmp_path: Path) -> None:
    result, calls = _run_routed(tmp_path, filters="*RealtimeRecordingTests")

    assert result.returncode == 0, result.stdout + result.stderr
    assert calls == [f"{CAPTURE_RUN} -Filter *RealtimeRecordingTests"]


def test_a_routed_itest_filter_of_a_hidden_class_runs_itest_in_gate(tmp_path: Path) -> None:
    result, calls = _run_routed(tmp_path, filters="*LifecycleTests")

    assert result.returncode == 0, result.stdout + result.stderr
    assert calls == ["pwsh -NoProfile -File run.ps1 itest -InGate -Filter *LifecycleTests"]


def test_a_routed_itest_runs_the_capture_group_after_a_failed_first_gate(tmp_path: Path) -> None:
    result, calls = _run_routed(tmp_path, statuses=[1, 0])

    assert result.returncode == 1, result.stdout + result.stderr
    assert calls == [HIDDEN_RUN, CAPTURE_RUN]


def test_itest_in_gate_refuses_the_capture_group(tmp_path: Path) -> None:
    result = _run_refusal(tmp_path, group_names="capture")

    assert result.returncode == 2, result.stdout + result.stderr
    assert VISIBLE_REFUSAL in result.stderr


def test_itest_in_gate_refuses_a_capture_class_filter(tmp_path: Path) -> None:
    result = _run_refusal(tmp_path, filters="*CaptureHelperTests")

    assert result.returncode == 2, result.stdout + result.stderr
    assert VISIBLE_REFUSAL in result.stderr


def _desktop_pattern(command: str) -> re.Pattern[str]:
    """A .gate/desktop.toml pattern as an anchored regex: '*' matches anything, '?' one character, the rest itself."""
    parts = []
    for character in command:
        if character == "*":
            parts.append(".*")
        elif character == "?":
            parts.append(".")
        else:
            parts.append(re.escape(character))
    return re.compile("^" + "".join(parts) + "$")


def _desktop_lists() -> tuple[list[str], list[str]]:
    data = tomllib.loads(DESKTOP.read_text(encoding="utf-8"))
    return list(data["hidden"]), list(data["patterns"])


def _matches(command: str, entries: list[str]) -> bool:
    return any(_desktop_pattern(entry).match(command) for entry in entries)


def test_the_hidden_group_run_moves_to_the_hidden_desktop() -> None:
    hidden, patterns = _desktop_lists()
    command = "pwsh -NoProfile -File run.ps1 itest -InGate -Groups lifecycle"

    assert _matches(command, hidden)
    assert not _matches(command, patterns)


def test_the_capture_runs_stay_on_a_visible_desktop() -> None:
    hidden, patterns = _desktop_lists()
    for command in (CAPTURE_RUN, f"{CAPTURE_RUN} -Filter *RealtimeRecordingTests"):
        assert _matches(command, patterns), command
        assert not _matches(command, hidden), command
