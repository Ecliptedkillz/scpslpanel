@echo off
setlocal

cd /d "%~dp0"

echo.
echo Starting SCP Control...
echo Dashboard: http://localhost:5080
echo Network:   http://YOUR-SERVER-IP:5080
echo Press Ctrl+C to stop.
echo.

dotnet run --project "src\ScpSlPanel.Api\ScpSlPanel.Api.csproj" --urls "http://0.0.0.0:5080"

if errorlevel 1 (
    echo.
    echo SCP Control stopped with an error.
    pause
    exit /b 1
)
