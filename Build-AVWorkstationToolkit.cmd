@echo off
setlocal
pushd "%~dp0"

echo Building AV Workstation Toolkit...
rem Let Windows PowerShell rebuild its own module path. This avoids inheriting
rem PowerShell 7-only modules when the build is started from pwsh or another tool.
set "PSModulePath="
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0build\Build-Release.ps1" %*
set "AVWORKSTATIONTOOLKIT_BUILD_EXIT=%ERRORLEVEL%"

if not "%AVWORKSTATIONTOOLKIT_BUILD_EXIT%"=="0" (
    echo.
    echo AV Workstation Toolkit build failed with exit code %AVWORKSTATIONTOOLKIT_BUILD_EXIT%.
    popd
    exit /b %AVWORKSTATIONTOOLKIT_BUILD_EXIT%
)

rem The build prints its own release folder: a beta's name (1.1.1-beta.2) is not in VERSION,
rem and an older folder for the same VERSION may still exist.
echo.
echo Build complete. The release folder and its files are listed above.

popd
exit /b 0
