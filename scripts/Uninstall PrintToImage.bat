@echo off
rem Removes PrintToImage from this computer. The saved PDF/JPG files are never deleted.
setlocal EnableExtensions
title Uninstall PrintToImage

net session >nul 2>&1
if not errorlevel 1 goto admin
echo Asking Windows for administrator rights...
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b
:admin
cd /d "%~dp0"

set "SCRIPT=%~dp0files\uninstall.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%ProgramFiles%\PrintToImage\uninstall.ps1"
if exist "%SCRIPT%" goto found
echo PrintToImage does not seem to be installed (uninstall.ps1 not found).
pause
exit /b 1
:found

echo.
echo  ============================================================
echo   PrintToImage - uninstall
echo  ============================================================
echo.
echo This removes the PrintToImage service, the shared printer, the firewall rules and the program.
echo The saved PDF/JPG files in the output folder are NOT deleted.
echo.
set "GO=N"
set /p "GO=Remove PrintToImage from this computer? [Y/N]: "
if /i not "%GO%"=="Y" goto cancelled

set "DATA=N"
echo.
echo Settings, logs and the archive of received print jobs are in C:\ProgramData\PrintToImage.
set /p "DATA=Delete them too? [Y/N]: "

set "ARGS="
if /i "%DATA%"=="Y" set "ARGS=-RemoveData"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %ARGS%
echo.
pause
exit /b 0

:cancelled
echo Nothing was changed.
pause
exit /b 0
