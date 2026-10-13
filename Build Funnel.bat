@echo off
REM ============================================================
REM  Double-click to build a fresh standalone Funnel.exe.
REM  Uses the .NET 8 SDK installed on this machine.
REM  The finished app is written to  dist\Funnel.exe
REM ============================================================
echo Building Funnel...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
echo.
echo Done. Your app is in the "dist" folder next to this file.
pause
