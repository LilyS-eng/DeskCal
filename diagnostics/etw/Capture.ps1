#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param([string]$OutputRoot = (Join-Path $PSScriptRoot 'results'))
$ErrorActionPreference = 'Stop'
$exeName = 'DeskCal.WpfDiagnostic'
if (Get-Process -Name $exeName -ErrorAction SilentlyContinue) { throw 'Exit the previous diagnostic first.' }
$start = Get-Date
$session = 'DeskCalGraphics-' + [guid]::NewGuid().ToString('N')
$dir = Join-Path $OutputRoot ($start.ToString('yyyyMMdd-HHmmss') + '-' + $PID)
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$etl = Join-Path $dir 'graphics.etl'
$status = Join-Path $dir 'capture.txt'
$pf = Join-Path $dir 'providers.txt'
$utc = $start.ToUniversalTime().ToString('o')
"StartUTC=$utc Session=$session OS=$([Environment]::OSVersion.Version)" | Set-Content $status
$providers = @('{E13B77A8-14B6-11DE-8069-001B212B5009} 0xffffffffffffffff 5')
foreach ($provider in 'Microsoft-Windows-Dwm-Core','Microsoft-Windows-DirectComposition','Microsoft-Windows-DXGI','Microsoft-Windows-D3D9','Microsoft-Windows-DxgKrnl') {
    & logman query providers $provider 2>&1 | Out-File (Join-Path $dir "$provider.txt")
    if ($LASTEXITCODE -eq 0) {
        $providers += "$provider 0xffffffffffffffff 5"
    } elseif ($provider -eq 'Microsoft-Windows-Dwm-Core') {
        throw 'DWM provider unavailable. Capture stopped.'
    } else { "Unavailable=$provider" | Add-Content $status }
}
$providers | Set-Content -Encoding ASCII $pf
$running = $false
try {
    & logman create trace $session -ets -pf $pf -o $etl -f bin -max 768 -bs 128 -nb 32 128
    if ($LASTEXITCODE -ne 0) { throw 'Could not start graphics ETW.' }
    $running = $true
    'Ready' | Add-Content $status
    Write-Host 'READY - Open DeskCal.WpfDiagnostic.exe normally from Explorer. Exit via its tray after the test.'
    $deadline = (Get-Date).AddMinutes(2)
    do {
        $app = Get-Process -Name $exeName -ErrorAction SilentlyContinue |
            Where-Object { $_.StartTime -ge $start -and $_.SessionId -eq (Get-Process -Id $PID).SessionId } | Select-Object -First 1
        if (!$app) { Start-Sleep -Milliseconds 150 }
    } until ($app -or (Get-Date) -ge $deadline)
    if (!$app) { throw 'No new diagnostic process during the two-minute launch window.' }
    "DiagnosticPID=$($app.Id) Path=$($app.Path)" | Add-Content $status
    if (!$app.WaitForExit(120000)) { 'Capture timed out after two minutes of application runtime.' | Add-Content $status }
} finally {
    if ($running) {
        & logman query $session -ets | Out-File (Join-Path $dir 'session-before-stop.txt')
        & logman stop $session -ets
        "StopExit=$LASTEXITCODE EndUTC=$([DateTime]::UtcNow.ToString('o'))" | Add-Content $status
    }
}
$log = Get-ChildItem "$env:LOCALAPPDATA/DeskCal/logs/acrylic-no-webview2-*-$($app.Id).log" |
    Where-Object { $_.LastWriteTime -ge $start } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (!$log) { throw "Matching application log unavailable. ETL retained in $dir" }
Copy-Item -LiteralPath $log.FullName -Destination $dir
if (!(Test-Path $etl) -or (Get-Item $etl).Length -eq 0) { throw "Missing ETL. See $dir" }
if ((Get-Item $etl).Length -ge 760MB) { 'Trace near size limit; check end coverage.' | Add-Content $status }
$zip = "$dir.zip"
Compress-Archive -Path "$dir/*" -DestinationPath $zip -CompressionLevel Fastest
Write-Host "RESULT: $zip"
