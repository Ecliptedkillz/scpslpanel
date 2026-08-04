@echo off
setlocal

cd /d "%~dp0"

if not exist ".env" (
    echo ERROR: .env was not found.
    echo Copy .env.example to .env and enter the deployment secrets.
    exit /b 1
)

for /f "usebackq eol=# tokens=1,* delims==" %%A in (".env") do (
    if not "%%A"=="" set "%%A=%%B"
)

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
