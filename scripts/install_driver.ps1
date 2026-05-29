$ProjectRoot = Split-Path -Parent $PSScriptRoot
$DriverDir = Join-Path $ProjectRoot "driver\x64"
$DevConPath = Join-Path $ProjectRoot "Source\scream_origin\scream\Install\helpers\devcon-x64.exe"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Install Scream Virtual Audio Driver" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check admin rights
$IsAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")
if (-not $IsAdmin) {
    Write-Host "!!! Administrator privileges required to install driver!" -ForegroundColor Red
    Write-Host "Please run this script as Administrator." -ForegroundColor Red
    exit 1
}

$InfFile = Join-Path $DriverDir "Scream.inf"
if (-not (Test-Path $InfFile)) {
    Write-Host "!!! INF file not found: $InfFile" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $DevConPath)) {
    Write-Host "!!! devcon-x64.exe not found at: $DevConPath" -ForegroundColor Red
    exit 1
}

# Step 1: Register driver to driver store
Write-Host ">>> [1/3] Registering driver to driver store..." -ForegroundColor Yellow
& pnputil /add-driver $InfFile /install 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "!!! pnputil failed" -ForegroundColor Red
    exit 1
}
Write-Host ""

# Step 2: Remove existing Scream device instance (if any)
Write-Host ">>> [2/3] Removing existing Scream device instance..." -ForegroundColor Yellow
& $DevConPath remove '*Scream' 2>&1
Write-Host ""

# Step 3: Create new Scream device instance
Write-Host ">>> [3/3] Creating Scream audio device..." -ForegroundColor Yellow
& $DevConPath install $InfFile '*Scream' 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "!!! devcon install failed" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Driver installation complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "Scream should now appear in:"
Write-Host "  - Device Manager > Sound, video and game controllers"
Write-Host "  - Sound Control Panel > Playback devices"
Write-Host "If not visible, right-click in Sound Control Panel and select"
Write-Host "'Show Disabled Devices', then enable Scream." -ForegroundColor Yellow
