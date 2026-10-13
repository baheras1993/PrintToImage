@echo off
rem Opens PrintToImage Settings (Windows asks for administrator rights).
setlocal
set "APP=%ProgramFiles%\PrintToImage\PrintToImageSettings.exe"
if not exist "%APP%" set "APP=%~dp0files\PrintToImageSettings.exe"
if not exist "%APP%" (
    echo PrintToImage Settings was not found. Install PrintToImage first ^(Install PrintToImage.bat^).
    pause
    exit /b 1
)
start "" "%APP%"
endlocal
