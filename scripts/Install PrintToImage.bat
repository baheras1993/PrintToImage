@echo off
rem Installs (or upgrades) PrintToImage on this server. Double-click this file.
setlocal EnableExtensions
title Install PrintToImage

rem ---- run as administrator (asks Windows for permission if needed)
net session >nul 2>&1
if not errorlevel 1 goto admin
echo Asking Windows for administrator rights...
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b
:admin
cd /d "%~dp0"

if exist "%~dp0files\install.ps1" goto found
echo The "files" folder was not found next to this file. Keep the PrintToImage folder together.
pause
exit /b 1
:found

echo.
echo  ============================================================
echo   PrintToImage - installation
echo  ============================================================
echo.

sc query PrintToImage >nul 2>&1
if errorlevel 1 goto newinstall
echo PrintToImage is already installed: it will be upgraded. Your settings are kept.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\install.ps1"
goto done

:newinstall
set "OUT=D:\PrintToImage\Output"
set /p "OUT=Folder for the saved PDF/JPG files [D:\PrintToImage\Output]: "

set "EMR="
echo.
echo Windows account or group of the program that reads the saved files,
echo for example an EMR or document system (e.g. OFFICE\svc-import).
set /p "EMR=Press Enter to skip and set it later in PrintToImage Settings: "

set "PAPER=A4"
echo.
set /p "PAPER=Paper size of the shared printer, A4 or Letter [A4]: "
if /i "%PAPER%"=="letter" set "PAPER=Letter"
if /i not "%PAPER%"=="Letter" set "PAPER=A4"

echo.
echo Installing with:
echo   Output folder : %OUT%
if defined EMR echo   Reader account: %EMR%
if not defined EMR echo   Reader account: none yet
echo   Paper size    : %PAPER%
echo.

set "EMRARG="
if defined EMR set EMRARG=-EmrAccount "%EMR%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\install.ps1" -OutputRoot "%OUT%" %EMRARG% -PaperSize %PAPER%

:done
if not errorlevel 1 goto success
echo.
echo  *** The installation did not finish. Read the message above. ***
echo.
pause
exit /b 1

:success
echo.
echo  Next steps:
echo   1. In PrintToImage Settings, list the PCs on the "Devices" tab.
echo   2. On each PC, add the shared printer \\%COMPUTERNAME%\PrintToImage
echo.
set "OPEN=Y"
set /p "OPEN=Open PrintToImage Settings now? [Y/N]: "
if /i "%OPEN%"=="Y" start "" "%ProgramFiles%\PrintToImage\PrintToImageSettings.exe"
endlocal
exit /b 0
