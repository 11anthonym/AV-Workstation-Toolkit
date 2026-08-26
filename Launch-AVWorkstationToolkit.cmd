@echo off
setlocal
if exist "%~dp0AVWorkstationToolkit.exe" (
    start "AV Workstation Toolkit" "%~dp0AVWorkstationToolkit.exe"
    exit /b 0
)
set /p AVWORKSTATIONTOOLKIT_VERSION=<"%~dp0VERSION"
if exist "%~dp0artifacts\release\%AVWORKSTATIONTOOLKIT_VERSION%\AV-Workstation-Toolkit-%AVWORKSTATIONTOOLKIT_VERSION%-win-x64.exe" (
    start "AV Workstation Toolkit" "%~dp0artifacts\release\%AVWORKSTATIONTOOLKIT_VERSION%\AV-Workstation-Toolkit-%AVWORKSTATIONTOOLKIT_VERSION%-win-x64.exe"
    exit /b 0
)
start "AV Workstation Toolkit" "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy RemoteSigned -STA -File "%~dp0scripts\Start-AVWorkstationToolkit.ps1"
endlocal
