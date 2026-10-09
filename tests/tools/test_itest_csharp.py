"""Tests for run.ps1 itest-csharp, the command the csharp itest group's gate runs, against a stand-in dotnet first on PATH."""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

import pytest

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"
# Records each call's arguments, prints them, and fails the helper's publish (GodotMcp.Dotnet.csproj) with status 3.
FAKE_DOTNET = "\r\n".join(
    [
        "@echo off",
        '>>"%FAKE_DOTNET_CALLS%" echo %*',
        "echo fake dotnet %*",
        'echo %* | findstr /c:"GodotMcp.Dotnet.csproj" >nul && exit /b 3',
        "exit /b 0",
        "",
    ]
)
# Records each call's arguments, prints them, passes every publish and build, and fails the test run with status 5.
FAKE_DOTNET_TEST_FAILS = "\r\n".join(
    [
        "@echo off",
        '>>"%FAKE_DOTNET_CALLS%" echo %*',
        "echo fake dotnet %*",
        'if "%1"=="test" exit /b 5',
        "exit /b 0",
        "",
    ]
)
# Stands in for tools/hidden-desktop.ps1: runs the command after its "--" on this desktop and exits with its status.
FAKE_HIDDEN_DESKTOP = "\n".join(
    [
        "$words = @($args)",
        "if ($words[0] -eq '--') { $words = @($words | Select-Object -Skip 1) }",
        "& $words[0] @($words | Select-Object -Skip 1)",
        "exit $LASTEXITCODE",
        "",
    ]
)
# Each staged publish file run.ps1 lays out, by its path under .tmp/dotnet-publish and the path it takes under bin/dotnet.
STAGED_FILES = {
    "shim/godot_mcp_dotnet.dll": "godot_mcp_dotnet.dll",
    "loader/GodotMcp.Dotnet.Loader.dll": "loader/GodotMcp.Dotnet.Loader.dll",
    "loader/GodotMcp.Dotnet.Loader.runtimeconfig.json": "loader/GodotMcp.Dotnet.Loader.runtimeconfig.json",
    "helper/GodotMcp.Dotnet.dll": "helper/GodotMcp.Dotnet.dll",
    "helper/GodotMcp.Dotnet.Core.dll": "helper/GodotMcp.Dotnet.Core.dll",
}
TEST_ASSEMBLY = "tests/GodotMcp.IntegrationTests/bin/Release/net10.0/GodotMcp.IntegrationTests.dll"
GROUP_CLASSES = " ".join(f"GodotMcp.IntegrationTests.{name}" for name in ["CSharpToolTests", "GameToolTests", "CSharpStateTests", "CSharpWatchTests"])


@pytest.mark.skipif(os.name != "nt", reason="the stand-in dotnet is a .cmd")
@pytest.mark.parametrize("test_filter", ["*CSharpStateTests", ""], ids=["filtered", "group"])
def test_the_group_lays_out_the_helper_builds_the_tests_then_runs_them_without_a_build_and_exits_with_their_status(
    tmp_path: Path, test_filter: str
) -> None:
    root = tmp_path / "repo"
    root.mkdir()
    shutil.copyfile(RUN_PS1, root / "run.ps1")
    (root / "tools").mkdir()
    (root / "tools" / "hidden-desktop.ps1").write_text(FAKE_HIDDEN_DESKTOP, encoding="utf-8", newline="\n")
    (root / "dotnet").mkdir()
    (root / "dotnet" / "godot_mcp_dotnet.gdextension").write_text("gdextension", encoding="utf-8", newline="\n")
    for staged in STAGED_FILES:
        path = root / ".tmp" / "dotnet-publish" / staged
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(staged, encoding="utf-8", newline="\n")
    # An earlier build's assembly, which may be stale against the sources, so the group builds even with it there.
    assembly = root / TEST_ASSEMBLY
    assembly.parent.mkdir(parents=True)
    assembly.write_text("built", encoding="utf-8", newline="\n")
    fake = tmp_path / "fake"
    fake.mkdir()
    (fake / "dotnet.cmd").write_text(FAKE_DOTNET_TEST_FAILS, encoding="utf-8", newline="")
    calls = tmp_path / "calls.txt"
    env = {**os.environ, "PATH": f"{fake}{os.pathsep}{os.environ['PATH']}", "FAKE_DOTNET_CALLS": str(calls)}

    command = ["pwsh", "-NoProfile", "-File", str(root / "run.ps1"), "itest-csharp"]
    if test_filter:
        command += ["-Filter", test_filter]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False, env=env)

    assert result.returncode == 5, result.stdout + result.stderr
    lines = calls.read_text(encoding="utf-8").splitlines()
    assert [line.split()[0] for line in lines] == ["publish", "publish", "publish", "build", "test"], lines
    assert "GodotMcp.IntegrationTests.csproj -c Release -warnaserror" in lines[3], lines
    test_call = lines[-1]
    assert "-c Release --no-build" in test_call, test_call
    assert test_call.endswith(f"--filter-class {test_filter or GROUP_CLASSES}"), test_call
    layout = root / "bin" / "dotnet"
    assert (layout / "godot_mcp_dotnet.gdextension").read_text(encoding="utf-8") == "gdextension"
    for staged, laid_out in STAGED_FILES.items():
        assert (layout / laid_out).read_text(encoding="utf-8") == staged, laid_out


@pytest.mark.skipif(os.name != "nt", reason="the stand-in dotnet is a .cmd")
def test_the_group_publishes_the_helper_itself_and_a_failed_publish_fails_it_naming_the_log(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    root.mkdir()
    shutil.copyfile(RUN_PS1, root / "run.ps1")
    fake = tmp_path / "fake"
    fake.mkdir()
    (fake / "dotnet.cmd").write_text(FAKE_DOTNET, encoding="utf-8", newline="")
    calls = tmp_path / "calls.txt"
    env = {**os.environ, "PATH": f"{fake}{os.pathsep}{os.environ['PATH']}", "FAKE_DOTNET_CALLS": str(calls)}

    command = ["pwsh", "-NoProfile", "-File", str(root / "run.ps1"), "itest-csharp"]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False, env=env)

    assert result.returncode == 3, result.stdout + result.stderr
    lines = calls.read_text(encoding="utf-8").splitlines()
    assert [line.split()[0] for line in lines] == ["publish", "publish", "publish"], lines
    projects = ["GodotMcp.Dotnet.Shim.csproj", "GodotMcp.Dotnet.Loader.csproj", "GodotMcp.Dotnet.csproj"]
    assert all(project in line for project, line in zip(projects, lines, strict=True)), lines
    log = root / ".tmp" / "dotnet-helper.log"
    assert f"itest-csharp: dotnet publish of helper failed (status 3); its log is {log}" in result.stdout
    assert "fake dotnet publish" in log.read_text(encoding="utf-8")
    assert not (root / "bin" / "dotnet").exists()
