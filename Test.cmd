@echo off
setlocal
pushd "%~dp0"
"%ProgramFiles%\dotnet\dotnet.exe" run --project Tests\Mio.Tests.csproj --verbosity minimal
set "RESULT=%ERRORLEVEL%"
popd
pause
exit /b %RESULT%
