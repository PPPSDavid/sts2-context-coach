param(
  [ValidateSet("list","approve","reject")] [string]$Action = "list",
  [string]$Id = "",
  [string]$Note = "",
  [string]$ConfigPath = ""
)

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$main = Join-Path $root "tools\data_refresh\main.py"
$configArgs = @()
if ($ConfigPath) {
  $configArgs = @("--config", $ConfigPath)
}

if ($Action -eq "list") {
  python $main heuristics-list @configArgs
  exit $LASTEXITCODE
}

if (-not $Id) {
  Write-Error "Provide -Id when using approve/reject."
  exit 1
}

$status = if ($Action -eq "approve") { "approved" } else { "rejected" }
python $main heuristics-set --id $Id --status $status --note $Note @configArgs
exit $LASTEXITCODE
