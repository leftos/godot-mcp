"""Tests for the FAIL lines run.ps1 prints after a failed gdtest run, read from the gate's stamped log."""

from __future__ import annotations

import subprocess
from pathlib import Path

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"

# Loads run.ps1's gdtest failure reader alone, without running the script, and prints what it writes for the log.
DRIVER = """param([string]$RunPs1, [string]$Log)
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($RunPs1, [ref]$null, [ref]$null)
$function = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Show-GdtestFailures'
}, $true)
if ($null -eq $function) {
    throw "run.ps1 has no function Show-GdtestFailures"
}
. ([scriptblock]::Create($function.Extent.Text))
Show-GdtestFailures -Log $Log
"""

STAMP = "2026-11-10T15:47:22.715220-08:00"
HEADER = "gdtest: FAIL lines from .tmp/gdtest.log:"
NO_FAIL = "gdtest: no FAIL line in .tmp/gdtest.log; read the log for the cause"
FIRST = "FAIL test_dormant.gd::test_a: expected 1"
SECOND = "FAIL test_dormant.gd: parse error"


def _show(tmp_path: Path, log_lines: list[str] | None) -> list[str]:
    driver = tmp_path / "driver.ps1"
    driver.write_text(DRIVER, encoding="utf-8", newline="\n")
    log = tmp_path / "gdtest.log"
    if log_lines is not None:
        log.write_text("\n".join(log_lines) + "\n", encoding="utf-8", newline="\n")

    command = ["pwsh", "-NoProfile", "-File", str(driver), "-RunPs1", str(RUN_PS1), "-Log", str(log)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)

    assert result.returncode == 0, result.stderr
    assert result.stderr == ""
    return result.stdout.splitlines()


def test_stamped_fail_lines_are_printed_without_their_stamp(tmp_path: Path) -> None:
    lines = [
        f"{STAMP} out ========== test_dormant.gd",
        f"{STAMP} out {SECOND}",
        f"{STAMP} err        [9] _initialize (res://run_tests.gd:64)",
        f"{STAMP} out {FIRST}",
        f"{STAMP} out gdtest: 848 passed, 2 failed",
    ]

    assert _show(tmp_path, lines) == [HEADER, SECOND, FIRST]


def test_a_bare_fail_line_is_printed(tmp_path: Path) -> None:
    assert _show(tmp_path, [FIRST]) == [HEADER, FIRST]


def test_a_line_holding_fail_mid_text_is_not_printed(tmp_path: Path) -> None:
    lines = [f"{STAMP} out ok: FAIL count 0", f"{STAMP} out gdtest: 848 passed, 0 failed"]

    assert _show(tmp_path, lines) == [NO_FAIL]


def test_a_stamped_log_with_no_fail_line_says_so(tmp_path: Path) -> None:
    assert _show(tmp_path, [f"{STAMP} out gdtest: 848 passed, 0 failed"]) == [NO_FAIL]


def test_a_missing_log_says_so(tmp_path: Path) -> None:
    assert _show(tmp_path, None) == [NO_FAIL]


# Prints each Invoke-Gated command run.ps1 gives the name 'gdtest', as PowerShell parsed it: one line per call, its
# words separated by a space.
GATE_DRIVER = """param([string]$RunPs1)
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($RunPs1, [ref]$null, [ref]$null)
$calls = $ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Invoke-Gated'
}, $true)
foreach ($call in $calls) {
    $words = @($call.CommandElements | ForEach-Object { $_.Extent.Text })
    $index = [array]::IndexOf($words, '-Name')
    if ($index -ge 0 -and $index + 1 -lt $words.Count -and $words[$index + 1] -eq "'gdtest'") {
        Write-Output ($words -join ' ')
    }
}
"""


def _gdtest_gate(tmp_path: Path) -> list[str]:
    """Runs the AST reader and returns one line per gate call the gdtest verb makes, its words space-separated."""
    driver = tmp_path / "gate-driver.ps1"
    driver.write_text(GATE_DRIVER, encoding="utf-8", newline="\n")

    command = ["pwsh", "-NoProfile", "-File", str(driver), "-RunPs1", str(RUN_PS1)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)

    assert result.returncode == 0, result.stderr
    assert result.stderr == ""
    return result.stdout.splitlines()


def test_the_gdtest_gate_runs_without_markers(tmp_path: Path) -> None:
    gates = _gdtest_gate(tmp_path)

    assert len(gates) == 1, gates
    assert "-NoMarkers" in gates[0].split(), gates[0]
