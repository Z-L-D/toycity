# ToyCity's GPU test (Windows). Builds the game, starts a server of its own on port 5189 with an empty save folder (so
# your own cities are never touched), runs the Godot client's --gpu-test and stops the server again. The report lands
# in gpu-test\ at the top of the repository, or in the folder given with -Out. Double-click gpu-test.cmd to run it.
param([string]$Out = "")
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $Out) { $Out = Join-Path $root "gpu-test" }
$godot = $env:GODOT
if (-not $godot) {
    $found = Get-Command "godot*.exe" -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike "*console*" } | Select-Object -First 1
    if ($found) { $godot = $found.Source }
}
if (-not $godot) {
    Write-Host "Couldn't find Godot. Set GODOT to your Godot 4.7.2 .NET exe, for example:"
    Write-Host '  setx GODOT "C:\Godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"'
    Write-Host "then run this again."
    exit 1
}
$port = 5189
$saves = Join-Path ([IO.Path]::GetTempPath()) ("toycity-gpu-test-" + [guid]::NewGuid())
New-Item -ItemType Directory $saves | Out-Null
Write-Host "Building the server and the client..."
dotnet build (Join-Path $root "src\ToyCity.Server") -c Release -v q --nologo; if ($LASTEXITCODE) { throw "The server didn't build." }
dotnet build (Join-Path $root "clients\godot") -v q --nologo; if ($LASTEXITCODE) { throw "The client didn't build." }
Write-Host "Starting a test server on port $port..."
$env:ToyCity__SaveDir = $saves
$dll = Join-Path $root "src\ToyCity.Server\bin\Release\net10.0\ToyCity.Server.dll"
$server = Start-Process dotnet -ArgumentList "`"$dll`"", "--urls", "http://localhost:$port" -WorkingDirectory $root -WindowStyle Minimized -PassThru `
    -RedirectStandardOutput (Join-Path $saves "server.log") -RedirectStandardError (Join-Path $saves "server.err")
try {
    for ($i = 0; $i -lt 60; $i++) {
        try { Invoke-WebRequest -UseBasicParsing "http://localhost:$port/health" | Out-Null; break } catch { Start-Sleep 1 }
    }
    & $godot --headless --path (Join-Path $root "clients\godot") --import | Out-Null
    Write-Host "Running the GPU test (about three minutes)..."
    & $godot --path (Join-Path $root "clients\godot") -- --server "ws://localhost:$port/ws" --gpu-test $Out | Out-Host
    Write-Host "The reports are in $Out"
}
finally {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $saves -ErrorAction SilentlyContinue
}
