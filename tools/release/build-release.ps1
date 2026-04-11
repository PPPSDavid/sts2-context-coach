param(
    [string]$Configuration = "Release",
    [string]$ProjectPath = "Sts2ContextCoach.csproj",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectFull = Join-Path $repoRoot $ProjectPath
$manifestPath = Join-Path $repoRoot "Sts2ContextCoach.json"
$releaseDir = Join-Path $repoRoot "release"
$stageDir = Join-Path $releaseDir "stage"

if (-not (Test-Path $manifestPath)) {
    throw "Missing manifest: $manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$modId = [string]$manifest.id
$version = [string]$manifest.version
if ([string]::IsNullOrWhiteSpace($modId) -or [string]::IsNullOrWhiteSpace($version)) {
    throw "Manifest must contain id and version."
}

if (-not $SkipBuild) {
    Write-Host "Building $ProjectPath ($Configuration)..."
    dotnet build "$projectFull" -c $Configuration --no-restore
}

$outDir = Join-Path $repoRoot ("bin\" + $Configuration + "\net9.0")
# Card/relic/keyword JSON is embedded in the DLL so STS2 does not scan extra *.json manifests under mods/.
$files = @(
    (Join-Path $outDir "Sts2ContextCoach.dll"),
    (Join-Path $outDir "Sts2ContextCoach.json"),
    (Join-Path $outDir "contextcoach.config"),
    (Join-Path $outDir "result_cleaned.csv")
)

foreach ($f in $files) {
    if (-not (Test-Path $f)) {
        throw "Missing build artifact: $f"
    }
}

if (Test-Path $stageDir) {
    Remove-Item $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

Copy-Item (Join-Path $outDir "Sts2ContextCoach.dll") (Join-Path $stageDir "Sts2ContextCoach.dll") -Force
Copy-Item (Join-Path $outDir "Sts2ContextCoach.json") (Join-Path $stageDir "Sts2ContextCoach.json") -Force
Copy-Item (Join-Path $outDir "contextcoach.config") (Join-Path $stageDir "contextcoach.config") -Force
Copy-Item (Join-Path $outDir "result_cleaned.csv") (Join-Path $stageDir "result_cleaned.csv") -Force

$zipName = "$modId-v$version.zip"
$zipPath = Join-Path $releaseDir $zipName
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Release package created:"
Write-Host $zipPath
