$ProjectRoot = Split-Path -Parent $PSScriptRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  AudioBridge - One-Click Build Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Build audio_player
Write-Host ">>> [1/2] Building Android audio player..." -ForegroundColor Yellow
$PlayerScript = Join-Path $ProjectRoot "audio_player\build_audio_player.ps1"
& $PlayerScript
if ($LASTEXITCODE -ne 0) {
    Write-Host "!!! audio_player build failed" -ForegroundColor Red
    exit 1
}

# Step 2: Build C# service
Write-Host ">>> [2/2] Building AudioBridge service..." -ForegroundColor Yellow
$ServiceDir = Join-Path $ProjectRoot "service\AudioBridge"
Push-Location $ServiceDir
dotnet build --configuration Release 2>&1
Pop-Location
if ($LASTEXITCODE -ne 0) {
    Write-Host "!!! AudioBridge build failed" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Build complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
