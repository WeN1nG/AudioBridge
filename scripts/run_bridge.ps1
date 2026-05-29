$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ServiceDir = Join-Path $ProjectRoot "service\AudioBridge"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  AudioBridge - Start Bridge Service" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Ensure Android device is connected with USB debugging enabled." -ForegroundColor Yellow
Write-Host "Audio will be captured from the default playback device via WASAPI Loopback." -ForegroundColor Yellow
Write-Host ""

Set-Location $ServiceDir
dotnet run --configuration Release --project $ServiceDir 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "!!! AudioBridge service exited abnormally" -ForegroundColor Red
    Write-Host "Press any key to close..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
