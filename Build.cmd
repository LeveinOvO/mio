@echo off
setlocal
pushd "%~dp0"
set /p MIO_VERSION=<"%~dp0VERSION"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Wait-ForPublishLock.ps1" -PublishDirectory "%~dp0artifacts\build\V%MIO_VERSION%"
if errorlevel 1 goto done
wsl.exe --exec bash -lc "cd -- \"$(wslpath -u '%~dp0.')\" && bash scripts/build-release.sh"
if errorlevel 1 goto done
:done
set "RESULT=%ERRORLEVEL%"
popd
pause
exit /b %RESULT%
