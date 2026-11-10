"""Tests for the long-running line run.ps1 prints when an itest group is killed, read from its diagnostic folder."""

from __future__ import annotations

import subprocess
from pathlib import Path

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"

# Loads run.ps1's long-running reader alone, without running the script, and prints what it finds under $LogDir.
DRIVER = """param([string]$RunPs1, [string]$LogDir)
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($RunPs1, [ref]$null, [ref]$null)
$function = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Show-ItestLongRunning'
}, $true)
if ($null -eq $function) {
    throw "run.ps1 has no function Show-ItestLongRunning"
}
. ([scriptblock]::Create($function.Extent.Text))
$logDir = $LogDir
Show-ItestLongRunning -Name 'itest-lifecycle'
"""

STAMP = "2026-11-10T13:31:00.1234567-08:00"
FIRST = "[Long Running Test] 'GodotMcp.IntegrationTests.SessionLifecycleTests.ALaunchStarts', Elapsed: 00:00:30"
SECOND = "[Long Running Test] 'GodotMcp.IntegrationTests.SessionLifecycleTests.ALaunchStarts', Elapsed: 00:01:00"


def _show(tmp_path: Path, diag: list[str] | None) -> tuple[Path, list[str]]:
    driver = tmp_path / "driver.ps1"
    driver.write_text(DRIVER, encoding="utf-8", newline="\n")
    folder = tmp_path / "itest-lifecycle.diag"
    if diag is not None:
        folder.mkdir()
        path = folder / "GodotMcp.IntegrationTests_20261110_131500.diag"
        path.write_text("\n".join(diag) + "\n", encoding="utf-8", newline="\n")

    command = ["pwsh", "-NoProfile", "-File", str(driver), "-RunPs1", str(RUN_PS1), "-LogDir", str(tmp_path)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)

    assert result.returncode == 0, result.stderr
    assert result.stderr == ""
    return folder, result.stdout.splitlines()


def test_the_last_long_running_line_is_printed_with_its_prefix(tmp_path: Path) -> None:
    _, printed = _show(tmp_path, [f"{STAMP} {FIRST}", f"{STAMP} {SECOND}", f"{STAMP} Test run finished"])

    assert printed == [f"itest itest-lifecycle: still running when it was killed: {STAMP} {SECOND}"]


def test_a_diag_folder_with_no_long_running_line_prints_nothing(tmp_path: Path) -> None:
    _, printed = _show(tmp_path, [f"{STAMP} Test run finished"])

    assert printed == []


def test_a_missing_diag_folder_prints_nothing(tmp_path: Path) -> None:
    _, printed = _show(tmp_path, None)

    assert printed == []
