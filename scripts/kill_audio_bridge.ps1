$procs = Get-Process -Name "AudioBridge" -ErrorAction SilentlyContinue
if (-not $procs) {
    # Fallback: look for dotnet process with AudioBridge args
    $procs = Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object {
        ($_.CommandLine -match "AudioBridge") -or ($_.CommandLine -match "AudioBridge.dll")
    }
}
if (-not $procs) {
    # Fallback: look in running process list by description
    $procs = Get-Process | Where-Object { $_.ProcessName -eq "AudioBridge" -or $_.ProcessName -eq "dotnet" } | Select-Object -First 1
}

if ($procs) {
    $procs | ForEach-Object {
        Write-Output "Killing AudioBridge PID $($_.Id)..."
        Stop-Process -Id $_.Id -Force
    }
    Start-Sleep 2
    $remaining = Get-Process -Name "AudioBridge" -ErrorAction SilentlyContinue
    if (-not $remaining) { Write-Output "Process terminated" }
    else { Write-Output "Process still running - may need admin" }
} else {
    Write-Output "AudioBridge process not found"
}
