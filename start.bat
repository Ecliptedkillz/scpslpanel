@echo off
setlocal

cd /d "%~dp0"

echo.
echo Starting SCP Control...
echo Dashboard: https://esbpanel.ezaiahhangout.com
echo Local API: http://127.0.0.1:5080
echo Press Ctrl+C to stop.
echo.

dotnet run --project "src\ScpSlPanel.Api\ScpSlPanel.Api.csproj" --urls "http://127.0.0.1:5080"

if errorlevel 1 (
    echo.
    echo SCP Control stopped with an error.
    pause
    exit /b 1
)
