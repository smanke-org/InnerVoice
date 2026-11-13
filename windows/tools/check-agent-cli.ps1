# Checks the `innervoice` CLI against the agent-cli rules on a test copy, the Windows twin of
# ~/.claude/skills/agent-cli/scripts/check-agent-cli.sh. Prints PASS or FAIL lines and exits 1
# on any FAIL.
#   pwsh tools/check-agent-cli.ps1 artifacts/win-x64/cli/innervoice.exe
# It starts a test copy of the app (APP_STORE in the temporary folder, INNERVOICE_DEMO=1 opens
# the sample book), never the person's own InnerVoice: the copy listens on a pipe of its own and
# keeps its files in that folder. At the end it stops that copy by its process id (from
# `status`), never by name, which would also close the person's own copy and their open book.
param([Parameter(Mandatory)] [string] $Cli)
$ErrorActionPreference = 'Continue'
$Cli = (Resolve-Path $Cli).Path
$env:APP_STORE = Join-Path ([IO.Path]::GetTempPath()) "agent-cli-check-$PID.store"
$env:INNERVOICE_DEMO = '1'
Remove-Item Env:APP_AGENT_PIPE -ErrorAction SilentlyContinue
$script:fails = 0
function Pass($what) { Write-Host "PASS $what" }
function Fail($what) { Write-Host "FAIL $what"; $script:fails++ }
function Json($text) { try { $text | ConvertFrom-Json -ErrorAction Stop } catch { $null } }

$out = & $Cli help 2>&1 | Out-String
if ($LASTEXITCODE -eq 0 -and $out.Trim()) { Pass 'help' } else { Fail 'help exits 0 and prints the commands' }

$schema = Json (& $Cli help --json | Out-String)
$ok = $schema -and $schema.commands.Count -gt 0
foreach ($c in $schema.commands) {
  if (-not ($c.description -and $c.description.Contains('Example:') -and $c.inputSchema.type -eq 'object')) { $ok = $false }
}
if ($ok) { Pass 'help --json: every command has a summary, an example, and a schema' } else { Fail 'help --json lists every command with a summary, example, and schema' }

& $Cli no-such-command *> $null
if ($LASTEXITCODE -eq 2) { Pass 'unknown command exits 2' } else { Fail 'unknown command exits 2' }
$err = Json (& $Cli no-such-command --json 2>$null | Out-String)
if ($err.error.code -and $err.error.message) { Pass 'errors with --json are {"error":{code,message}} on stdout' } else { Fail 'errors with --json are {"error":{code,message}} on stdout' }

# Starts the test copy, then every read-only command without required params runs and prints JSON.
$raw = & $Cli status --json 2>&1 | Out-String
$status = Json $raw
if ($status.testRun -eq $true -and $status.pid) { Pass 'status starts a test copy (testRun, pid)' } else { Fail "status starts a test copy and says testRun (got: $($raw.Trim()))" }
foreach ($c in $schema.commands) {
  if ($c.readOnly -and @($c.inputSchema.required).Count -eq 0) {
    $words = $c.name -split ' '
    $res = & $Cli @words --json 2>$null | Out-String
    $code = $LASTEXITCODE
    if ((Json $res) -and $code -eq 0) { Pass "$($c.name) --json" } else { Fail "$($c.name) --json exits 0 with one JSON object (got exit $code)" }
  }
}

$requests = @(
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}',
  '{"jsonrpc":"2.0","method":"notifications/initialized"}',
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
)
$lines = @($requests | & $Cli mcp 2>$null) | Where-Object { $_.Trim() } | ForEach-Object { Json $_ }
$ok = $lines.Count -eq 2 -and $null -ne $lines[0].result.capabilities.tools -and $lines[1].result.tools.Count -eq $schema.commands.Count
foreach ($t in $lines[1].result.tools) {
  if ($t.name -notmatch '^[A-Za-z0-9_-]{1,64}$' -or $null -eq $t.annotations.readOnlyHint) { $ok = $false }
}
if ($ok) { Pass 'mcp: initialize and tools/list, one tool per command' } else { Fail 'mcp answers initialize and tools/list with one tool per command' }

# An innervoice:// link reaches the running copy (through the CLI, as a second app start would).
& $Cli 'innervoice://open/review' *> $null
$front = Json (& $Cli projects list --json 2>$null | Out-String)
if ($LASTEXITCODE -eq 0 -and $front.projects[0].page -eq 'review') { Pass 'innervoice://open/review opens the Review page' } else { Fail 'innervoice:// links reach the running copy' }

# Quit the test copy only: the process that answered on the test pipe.
if ($status.pid) { Stop-Process -Id $status.pid -ErrorAction SilentlyContinue }
Remove-Item -Recurse -Force $env:APP_STORE -ErrorAction SilentlyContinue

if ($script:fails -eq 0) { Write-Host 'PASS agent CLI'; exit 0 }
Write-Host "FAIL agent CLI: $($script:fails) checks"; exit 1
