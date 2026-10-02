#requires -Version 7
<#
.SYNOPSIS
The prek hook `agent-mail-guard`: hands the staged paths to the user-level Agent Mail lease guard when the machine has
one, and passes when it does not.

.DESCRIPTION
The guard, `$env:CLAUDE_CONFIG_DIR\tools\agent-mail\guard-check.ps1` or the user's `~/.claude\tools\agent-mail\
guard-check.ps1` when that variable is unset, blocks a commit whose staged paths sit under another Claude Code session's
exclusive Agent Mail lease. A clone on a machine without it has no sessions to collide with, so its commits pass; the
hook says so on standard error, one line, so a skipped check is never read as a passed one.

Usage: pwsh -NoProfile -File tools/agent-mail-guard.ps1 <path>...
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$guardPath = Join-Path ($env:CLAUDE_CONFIG_DIR ?? (Join-Path $HOME '.claude')) 'tools/agent-mail/guard-check.ps1'

if (-not (Test-Path -LiteralPath $guardPath)) {
    [Console]::Error.WriteLine("agent-mail-guard: $guardPath not found; no lease check on this machine")
    exit 0
}
& $guardPath @args
exit ([int]$LASTEXITCODE)
