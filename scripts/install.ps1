param(
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtime = "win-x64"
$configuration = "Release"

$instantProject = Join-Path $repoRoot "companion\TaskbarInstantSearch\TaskbarInstantSearch.csproj"

$instantInstallDir = Join-Path $env:APPDATA "TaskbarInstantSearch"

dotnet publish $instantProject -c $configuration -r $runtime --self-contained true

$instantPublishDir = Join-Path $repoRoot "companion\TaskbarInstantSearch\bin\$configuration\net8.0-windows\$runtime\publish"

New-Item -ItemType Directory -Force -Path $instantInstallDir | Out-Null

Get-Process TaskbarInstantSearch -ErrorAction SilentlyContinue | Stop-Process -Force

Copy-Item -Path (Join-Path $instantPublishDir "*") -Destination $instantInstallDir -Recurse -Force

$configPath = Join-Path $instantInstallDir "config.json"
if (-not (Test-Path $configPath)) {
    Copy-Item -Path (Join-Path $repoRoot "config.example.json") -Destination $configPath
}

$promptPath = Join-Path $instantInstallDir "ai-prompt.md"
if (-not (Test-Path $promptPath)) {
    Copy-Item -Path (Join-Path $repoRoot "ai-prompt.example.md") -Destination $promptPath
}

$instantExe = Join-Path $instantInstallDir "TaskbarInstantSearch.exe"
$runPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $runPath -Name "TaskbarInstantSearch" -Value ('"' + $instantExe + '"')

if (-not $NoStart) {
    Start-Process -FilePath $instantExe -WorkingDirectory $instantInstallDir -WindowStyle Hidden
}

Write-Host "Installed TaskbarInstantSearch to $instantInstallDir"
Write-Host "Config: $configPath"
Write-Host "Prompt: $promptPath"
Write-Host "Optional Windhawk mod: $(Join-Path $repoRoot 'mods\taskbar-search-box-position-v1.wh.cpp')"
