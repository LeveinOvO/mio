@echo off
setlocal
set "RUNOPTION="
if /i "%~1"=="--check" set "RUNOPTION=-CheckOnly"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-App.ps1" %RUNOPTION%
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" pause
exit /b %RESULT%
