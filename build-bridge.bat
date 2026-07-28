@echo off
setlocal

cd /d "%~dp0"

if not defined SCPSL_MANAGED (
    set /p "SCPSL_MANAGED=Enter the full path to SCP Secret Laboratory\SCPSL_Data\Managed: "
)

if not exist "%SCPSL_MANAGED%\LabApi.dll" (
    echo.
    echo LabApi.dll was not found in:
    echo %SCPSL_MANAGED%
    pause
    exit /b 1
)

if not exist "%SCPSL_MANAGED%\Mirror.dll" (
    echo.
    echo Mirror.dll was not found in:
    echo %SCPSL_MANAGED%
    echo Confirm this is the live SCP Secret Laboratory\SCPSL_Data\Managed folder.
    pause
    exit /b 1
)

dotnet build "src\ScpSlPanel.LabApiBridge\ScpSlPanel.LabApiBridge.csproj" -c Release -p:ScpSlManagedPath="%SCPSL_MANAGED%"
if errorlevel 1 (
    echo.
    echo Bridge build failed.
    pause
    exit /b 1
)

echo.
echo Bridge built successfully:
echo src\ScpSlPanel.LabApiBridge\bin\Release\net48\ScpSlPanel.LabApiBridge.dll
pause
