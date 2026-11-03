@echo off
rem Double-click to run ToyCity's GPU test. See gpu-test.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gpu-test.ps1" %*
pause
