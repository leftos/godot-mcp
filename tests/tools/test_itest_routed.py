"""Tests for run.ps1's check that a routed itest ran: the gate's verdict read from .tmp/itest-gate.log."""

from __future__ import annotations

import subprocess
from pathlib import Path

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"

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

# Runs Invoke-ItestRouted against a stub gate wrapper that writes the given log and exits with the given status, and
# exits with the function's status.
ROUTED_DRIVER = (
    """param([string]$RunPs1, [string]$Root, [string]$Stub)
$ErrorActionPreference = 'Stop'
$Names = @('Get-GateKillLine', 'Get-GateKill', 'Get-GateNoRunLine', 'Invoke-ItestRouted')
"""
    + LOADER
    + """function Get-ItestRefusal { return '' }
$root = $Root
$logDir = Join-Path $Root '.tmp'
$gateWrapper = $Stub
$pwshPath = [Environment]::ProcessPath
$itestRoutedCeiling = 1800
exit (Invoke-ItestRouted -Filters @() -Since '' -GroupNames @())
"""
)

# The stub gate wrapper: writes its log lines to the --log path and exits with the given status.
STUB = """$log = $args[[array]::IndexOf($args, '--log') + 1]
Set-Content -LiteralPath $log -Value @(LINES) -Encoding utf8
Write-Output 'stub gate ran'
exit STATUS
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
    root = tmp_path / "repo"
    (root / ".tmp").mkdir(parents=True)
    stub = tmp_path / "gate.ps1"
    quoted = ", ".join("'" + _stamped(line).replace("'", "''") + "'" for line in [BUSY])
    stub.write_text(STUB.replace("LINES", quoted).replace("STATUS", "0"), encoding="utf-8", newline="\n")
    driver = tmp_path / "driver.ps1"
    driver.write_text(ROUTED_DRIVER, encoding="utf-8", newline="\n")

    command = [
        "pwsh",
        "-NoProfile",
        "-File",
        str(driver),
        "-RunPs1",
        str(RUN_PS1),
        "-Root",
        str(root),
        "-Stub",
        str(stub),
    ]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)

    assert result.returncode == 1, result.stdout + result.stderr
    lines = result.stdout.splitlines()
    assert "stub gate ran" in lines
    assert lines[-1] == f"itest: no test ran: {BUSY}"
