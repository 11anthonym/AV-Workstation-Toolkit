@echo off
setlocal
if exist "%~dp0AVWorkstationToolkit.exe" (
    start "AV Workstation Toolkit" "%~dp0AVWorkstationToolkit.exe"
    exit /b 0
)
set /p AVWORKSTATIONTOOLKIT_VERSION=<"%~dp0VERSION"
rem Start the most recently built release of VERSION: 1.1.1 itself or a pre-release such as 1.1.1-beta.2.
rem The wildcard also matches 1.1.10, so a folder counts only when VERSION is its whole name or is followed by "-".
setlocal EnableDelayedExpansion
set "AVWORKSTATIONTOOLKIT_RELEASE="
for /f "delims=" %%R in ('dir /b /a:d /o:d "%~dp0artifacts\release\%AVWORKSTATIONTOOLKIT_VERSION%*" 2^>nul') do (
    set "AVWORKSTATIONTOOLKIT_SUFFIX=%%R"
    set "AVWORKSTATIONTOOLKIT_SUFFIX=!AVWORKSTATIONTOOLKIT_SUFFIX:*%AVWORKSTATIONTOOLKIT_VERSION%=!"
    if exist "%~dp0artifacts\release\%%R\AV-Workstation-Toolkit-%%R-win-x64.exe" (
        if "!AVWORKSTATIONTOOLKIT_SUFFIX!"=="" set "AVWORKSTATIONTOOLKIT_RELEASE=%%R"
        if "!AVWORKSTATIONTOOLKIT_SUFFIX:~0,1!"=="-" set "AVWORKSTATIONTOOLKIT_RELEASE=%%R"
    )
)
if defined AVWORKSTATIONTOOLKIT_RELEASE (
    start "AV Workstation Toolkit" "%~dp0artifacts\release\%AVWORKSTATIONTOOLKIT_RELEASE%\AV-Workstation-Toolkit-%AVWORKSTATIONTOOLKIT_RELEASE%-win-x64.exe"
    exit /b 0
)
endlocal
where.exe dotnet.exe >nul 2>&1
if errorlevel 1 (
    echo AV Workstation Toolkit has not been built and the .NET 10 SDK is unavailable.
    echo Run Build-AVWorkstationToolkit.cmd from a configured development workstation.
    exit /b 1
)
start "AV Workstation Toolkit" /D "%~dp0" dotnet.exe run --project "%~dp0src\AVWorkstationToolkit.App\AVWorkstationToolkit.App.csproj" --configuration Release
endlocal
