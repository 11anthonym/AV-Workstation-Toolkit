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
where.exe dotnet.exe >nul 2>&1
if errorlevel 1 (
    echo AV Workstation Toolkit has not been built and the .NET 10 SDK is unavailable.
    echo Run Build-AVWorkstationToolkit.cmd from a configured development workstation.
    exit /b 1
)
start "AV Workstation Toolkit" /D "%~dp0" dotnet.exe run --project "%~dp0src\AVWorkstationToolkit.App\AVWorkstationToolkit.App.csproj" --configuration Release
endlocal
