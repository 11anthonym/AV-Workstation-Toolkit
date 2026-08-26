@echo off
setlocal
pushd "%~dp0"

echo Building AV Workstation Toolkit...
rem Let Windows PowerShell rebuild its own module path. This avoids inheriting
rem PowerShell 7-only modules when the build is started from pwsh or Codex.
set "PSModulePath="
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0build\Build-Release.ps1" %*
set "AVWORKSTATIONTOOLKIT_BUILD_EXIT=%ERRORLEVEL%"

if not "%AVWORKSTATIONTOOLKIT_BUILD_EXIT%"=="0" (
    echo.
    echo AV Workstation Toolkit build failed with exit code %AVWORKSTATIONTOOLKIT_BUILD_EXIT%.
    popd
    exit /b %AVWORKSTATIONTOOLKIT_BUILD_EXIT%
)

set /p AVWORKSTATIONTOOLKIT_VERSION=<"%~dp0VERSION"
echo.
echo Build complete.
echo Standalone app: artifacts\release\%AVWORKSTATIONTOOLKIT_VERSION%\AV-Workstation-Toolkit-%AVWORKSTATIONTOOLKIT_VERSION%-win-x64.exe
echo Installer:      artifacts\release\%AVWORKSTATIONTOOLKIT_VERSION%\AV-Workstation-Toolkit-%AVWORKSTATIONTOOLKIT_VERSION%-x64.msi
if exist "%~dp0artifacts\release\%AVWORKSTATIONTOOLKIT_VERSION%\AV-Workstation-Toolkit-%AVWORKSTATIONTOOLKIT_VERSION%-offline-bundle.zip" echo Offline bundle: artifacts\release\%AVWORKSTATIONTOOLKIT_VERSION%\AV-Workstation-Toolkit-%AVWORKSTATIONTOOLKIT_VERSION%-offline-bundle.zip

popd
exit /b 0
