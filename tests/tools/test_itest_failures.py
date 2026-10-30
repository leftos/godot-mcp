"""Tests for the failure blocks run.ps1 prints after a failed itest group, read from that group's lane log."""

from __future__ import annotations

import subprocess
from pathlib import Path

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"

# Loads run.ps1's two failure-block functions alone, without running the script, and prints a lane log's blocks.
DRIVER = """param([string]$RunPs1, [string]$Log)
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($RunPs1, [ref]$null, [ref]$null)
foreach ($name in 'Get-ItestFailureBlock', 'Show-ItestFailureBlock') {
    $function = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $function) {
        throw "run.ps1 has no function $name"
    }
    . ([scriptblock]::Create($function.Extent.Text))
}
Show-ItestFailureBlock -Group lifecycle -Log $Log
"""

STAMP = "2026-10-30T02:21:47.378864-07:00"
ASSEMBLY = r"D:\gate-remote\work\x\tests\GodotMcp.IntegrationTests\bin\Release\net10.0\GodotMcp.IntegrationTests.dll (net10.0|x64)"
FIRST_BLOCK = [
    "failed GodotMcp.IntegrationTests.SessionLifecycleTests.ALaunchStarts (2s 100ms)",
    f"  from {ASSEMBLY}",
    r"  GodotMcp.Server.Session.SessionException: Launching C:\p\game failed: Godot exited with code -1066598273 before the bridge connected.",
    "    ",
    r"    at GodotMcp.IntegrationTests.SessionLifecycleTests.ALaunchStarts() in X:\t\SessionLifecycleTests.cs:40",
]
SECOND_BLOCK = [
    "failed GodotMcp.IntegrationTests.SessionLifecycleTests.ARestartStarts (1s 5ms)",
    f"  from {ASSEMBLY}",
    "  Assert.Equal() Failure: Values differ",
    r"    at GodotMcp.IntegrationTests.SessionLifecycleTests.ARestartStarts() in X:\t\SessionLifecycleTests.cs:80",
]
SUMMARY = [
    f"{ASSEMBLY} failed with 2 error(s) (9s 1ms)",
    "",
    "Test run summary: Failed!",
    "  failed: 2",
]


def _stamped(stream: str, line: str) -> str:
    """A line as the gate writes it: stamped with its time and stream, an empty line as the bare stream."""
    return f"{STAMP} {stream} {line}" if line else f"{STAMP} {stream}"


def _lane_log(*blocks: list[str]) -> list[str]:
    lines = [_stamped("gat", "gate: running dotnet test"), _stamped("out", f"Running tests from {ASSEMBLY}")]
    for block in blocks:
        lines += [_stamped("out", line) for line in block]
        lines.append(_stamped("out", ""))
    lines += [_stamped("out", line) for line in SUMMARY]
    lines.append(_stamped("gat", "gate: FAILED (status 2)"))
    return lines


def _show(tmp_path: Path, log_lines: list[str] | None) -> tuple[Path, list[str]]:
    driver = tmp_path / "driver.ps1"
    driver.write_text(DRIVER, encoding="utf-8", newline="\n")
    log = tmp_path / "itest-lifecycle.log"
    if log_lines is not None:
        log.write_text("\n".join(log_lines) + "\n", encoding="utf-8", newline="\n")

    command = ["pwsh", "-NoProfile", "-File", str(driver), "-RunPs1", str(RUN_PS1), "-Log", str(log)]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)

    assert result.returncode == 0, result.stderr
    assert result.stderr == ""
    return log, result.stdout.splitlines()


def _header(log: Path) -> str:
    return f"itest lifecycle: failed tests from {log}:"


def _block(test: str, lines: int) -> list[str]:
    return [f"failed GodotMcp.IntegrationTests.SessionLifecycleTests.{test} (1s 0ms)", *(f"    at frame {index}" for index in range(1, lines))]


def test_a_failed_groups_lane_log_prints_each_failed_tests_block(tmp_path: Path) -> None:
    log, printed = _show(tmp_path, _lane_log(FIRST_BLOCK, SECOND_BLOCK))

    assert printed == [_header(log), *FIRST_BLOCK, *SECOND_BLOCK]


def test_a_single_failed_test_prints_its_whole_block(tmp_path: Path) -> None:
    log, printed = _show(tmp_path, _lane_log(FIRST_BLOCK))

    assert printed == [_header(log), *FIRST_BLOCK]


def test_a_gate_line_inside_a_block_is_dropped_and_does_not_end_it(tmp_path: Path) -> None:
    lines = _lane_log(FIRST_BLOCK)
    lines.insert(4, _stamped("gat", "exit pid=52604 code=3228369023 (0xC06D007F) ran=0.091450s"))

    log, printed = _show(tmp_path, lines)

    assert printed == [_header(log), *FIRST_BLOCK]


def test_a_block_over_40_lines_is_cut_to_40_saying_what_is_left(tmp_path: Path) -> None:
    long_block = _block("ALongFailure", 50)

    log, printed = _show(tmp_path, _lane_log(long_block))

    assert printed == [_header(log), *long_block[:39], "  ... 11 more lines in the log"]


def test_at_most_5_failed_tests_are_printed_and_the_rest_counted(tmp_path: Path) -> None:
    blocks = [_block(f"Failure{index}", 2) for index in range(1, 8)]

    log, printed = _show(tmp_path, _lane_log(*blocks))

    assert printed == [_header(log), *(line for block in blocks[:5] for line in block), "itest lifecycle: 2 more failed tests in the log"]


def test_a_log_with_no_failed_test_says_so(tmp_path: Path) -> None:
    log, printed = _show(tmp_path, _lane_log())

    assert printed == [f"itest lifecycle: no failed test in {log}; read the log for the cause"]


def test_a_missing_log_says_it_could_not_be_read_without_throwing(tmp_path: Path) -> None:
    log, printed = _show(tmp_path, None)

    assert len(printed) == 1
    assert printed[0].startswith(f"itest lifecycle: could not read {log}: "), printed
    assert "itest-lifecycle.log" in printed[0][len(f"itest lifecycle: could not read {log}: ") :], printed
