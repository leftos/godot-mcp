"""Tests for run.ps1's reading of the gate's kill line in a log, stamped as the gate writes its log or bare."""

from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"

# Loads run.ps1's kill-line functions alone, without running the script, and prints the kind and the reading.
DRIVER = """param([string]$RunPs1, [string]$Log)
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($RunPs1, [ref]$null, [ref]$null)
foreach ($name in 'Get-GateKillLine', 'Get-GateKillKind', 'Get-GateKill') {
    $function = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $function) {
        throw "run.ps1 has no function $name"
    }
    . ([scriptblock]::Create($function.Extent.Text))
}
Write-Output "kind=$(Get-GateKillKind -Log $Log)"
Write-Output "kill=$(Get-GateKill -Log $Log)"
"""

STAMP = "2026-11-10T07:07:11.460395-08:00"
STALLED = (
    "gate: STALLED: no output and 0.25 s of CPU (under the 1.20 s floor) for 120 s not waiting for a processor "
    "(machine free 100% on average); killed pwsh -NoProfile -File run.ps1 itest -InGate"
)


def _read(tmp_path: Path, log_lines: list[str]) -> tuple[str, str]:
    driver = tmp_path / "driver.ps1"
    driver.write_text(DRIVER, encoding="utf-8", newline="\n")
    log = tmp_path / "itest-gate.log"
    log.write_text("".join(f"{line}\n" for line in log_lines), encoding="utf-8", newline="\n")

    command = ["pwsh", "-NoProfile", "-File", str(driver), "-RunPs1", str(RUN_PS1), "-Log", str(log)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)

    assert result.returncode == 0, result.stderr
    assert result.stderr == ""
    kind, kill = result.stdout.splitlines()
    return kind, kill


@pytest.mark.parametrize("prefix", [f"{STAMP} gat ", f"{STAMP} err ", f"{STAMP} out ", ""], ids=["stamped", "err", "out", "bare"])
def test_a_stalled_kill_line_is_read_stamped_or_bare(tmp_path: Path, prefix: str) -> None:
    lines = [f"{STAMP} gat gate: running pwsh", f"{prefix}{STALLED}"]

    kind, kill = _read(tmp_path, lines)

    assert kind == "kind=STALLED"
    assert kill == f"kill={STALLED} It hung: read {tmp_path / 'itest-gate.log'} for where it stopped."


def test_a_log_with_no_kill_line_has_no_kind(tmp_path: Path) -> None:
    kind, kill = _read(tmp_path, [f"{STAMP} gat gate: running pwsh"])

    assert kind == "kind="
    assert kill == f"kill=no kill line in {tmp_path / 'itest-gate.log'}"
