@echo off
setlocal
cd /d "%~dp0"

echo Publishing SCP Control Launcher...
dotnet publish "launcher\ScpControl.Launcher.csproj" -c Release -r win-x64 --self-contained true -o "launcher\publish"

if errorlevel 1 (
    echo.
    echo Launcher build failed.
    pause
    exit /b 1
)

echo.
echo Launcher published to launcher\publish\SCP Control Launcher.exe
pause
