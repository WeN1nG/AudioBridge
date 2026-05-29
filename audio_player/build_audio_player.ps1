$ProjectRoot = Split-Path -Parent $PSScriptRoot
$NdkRoot = Join-Path $ProjectRoot "Source\ndk"
$NdkDir = Join-Path $NdkRoot "android-ndk-r27d"
$NdkZip = Join-Path $NdkRoot "android-ndk-r27d-windows.zip"
$NdkUrl = "https://dl.google.com/android/repository/android-ndk-r27d-windows.zip"
$PlayerDir = Join-Path $ProjectRoot "audio_player"
$Toolchain = Join-Path $NdkDir "toolchains\llvm\prebuilt\windows-x86_64"
$CC = Join-Path $Toolchain "bin\aarch64-linux-android26-clang.cmd"

Write-Host "[build_audio_player] start" -ForegroundColor Cyan

# Auto-download NDK if missing
if (-not (Test-Path $NdkDir)) {
    Write-Host "[build_audio_player] NDK not found at: $NdkDir" -ForegroundColor Yellow
    Write-Host "[build_audio_player] Downloading NDK r27d from Google..." -ForegroundColor Yellow
    if (-not (Test-Path $NdkRoot)) { New-Item -ItemType Directory -Path $NdkRoot -Force | Out-Null }

    try {
        # Download with progress bar
        $ProgressPreference = 'Continue'
        Invoke-WebRequest -Uri $NdkUrl -OutFile $NdkZip -UseBasicParsing
        Write-Host "[build_audio_player] Download complete, extracting..." -ForegroundColor Green

        # Extract zip
        Expand-Archive -Path $NdkZip -DestinationPath $NdkRoot -Force
        Remove-Item $NdkZip

        Write-Host "[build_audio_player] NDK extracted to: $NdkDir" -ForegroundColor Green
    } catch {
        Write-Host "[build_audio_player] Failed to download NDK: $_" -ForegroundColor Red
        Write-Host "[build_audio_player] Please download manually from:" -ForegroundColor Yellow
        Write-Host "[build_audio_player] $NdkUrl" -ForegroundColor Yellow
        Write-Host "[build_audio_player] Extract to: $NdkDir" -ForegroundColor Yellow
        exit 1
    }
}

Write-Host "[build_audio_player] Compiler: $CC"

# Clean old binary
$OutputBin = Join-Path $PlayerDir "audio_player"
if (Test-Path $OutputBin) { Remove-Item $OutputBin }

# Compile directly (single file, no make needed)
Write-Host "[build_audio_player] Compiling..."
& $CC -Oz -Wno-unused-parameter -o "$PlayerDir\audio_player" "$PlayerDir\audio_player.c" -laaudio -llog -lm 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "[build_audio_player] Compilation failed (exit code: $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}

$OutputBin = Join-Path $PlayerDir "audio_player"
if (Test-Path $OutputBin) {
    $file = Get-Item $OutputBin
    Write-Host "[build_audio_player] Build SUCCESS: $($file.Length) bytes" -ForegroundColor Green
} else {
    Write-Host "[build_audio_player] Build FAILED - output not found" -ForegroundColor Red
    exit 1
}

Write-Host "[build_audio_player] end"
