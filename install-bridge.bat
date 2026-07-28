@echo off
setlocal

cd /d "%~dp0"

echo.
echo SCP Control LabAPI Bridge Installer
echo -----------------------------------
echo Copy the Server ID and token from the server's Players tab.
echo.

if not defined SCPSL_MANAGED set /p "SCPSL_MANAGED=SCPSL_Data\Managed path: "
if not exist "%SCPSL_MANAGED%\LabApi.dll" (
    echo LabApi.dll was not found in "%SCPSL_MANAGED%".
    pause
    exit /b 1
)

set "SERVER_PORT=7777"
set /p "SERVER_PORT=Server port [7777]: "
if not defined SERVER_PORT set "SERVER_PORT=7777"

set "LABAPI_ROOT=%APPDATA%\SCP Secret Laboratory\LabAPI"
echo Default LabAPI data folder:
echo %LABAPI_ROOT%
set /p "CUSTOM_ROOT=Press Enter to use it, or enter a different LabAPI folder: "
if defined CUSTOM_ROOT set "LABAPI_ROOT=%CUSTOM_ROOT%"

set /p "PANEL_URL=Panel URL reachable by the game server (example http://192.168.1.20:5080): "
set /p "SERVER_ID=Server ID from the Players tab: "
set /p "BRIDGE_TOKEN=Bridge token from the Players tab: "

if not defined PANEL_URL goto :missing
if not defined SERVER_ID goto :missing
if not defined BRIDGE_TOKEN goto :missing

echo.
echo Building bridge against the installed LabAPI version...
dotnet build "src\ScpSlPanel.LabApiBridge\ScpSlPanel.LabApiBridge.csproj" -c Release -p:ScpSlManagedPath="%SCPSL_MANAGED%"
if errorlevel 1 goto :error

set "PLUGIN_DIR=%LABAPI_ROOT%\plugins\%SERVER_PORT%"
set "CONFIG_DIR=%LABAPI_ROOT%\configs\%SERVER_PORT%\SCP Control Bridge"
if not exist "%PLUGIN_DIR%" mkdir "%PLUGIN_DIR%"
if errorlevel 1 goto :error
if not exist "%CONFIG_DIR%" mkdir "%CONFIG_DIR%"
if errorlevel 1 goto :error

copy /y "src\ScpSlPanel.LabApiBridge\bin\Release\net48\ScpSlPanel.LabApiBridge.dll" "%PLUGIN_DIR%\ScpSlPanel.LabApiBridge.dll" >nul
if errorlevel 1 goto :error

(
    echo panel_url: "%PANEL_URL%"
    echo server_id: "%SERVER_ID%"
    echo token: "%BRIDGE_TOKEN%"
    echo heartbeat_seconds: 5
    echo respect_do_not_track: true
) > "%CONFIG_DIR%\scp-control-bridge.yml"
if errorlevel 1 goto :error

echo.
echo Bridge installed successfully.
echo Plugin: %PLUGIN_DIR%\ScpSlPanel.LabApiBridge.dll
echo Config: %CONFIG_DIR%\scp-control-bridge.yml
echo.
echo Restart the SCP:SL server, then open the Players tab.
pause
exit /b 0

:missing
echo.
echo Panel URL, Server ID, and bridge token are required.
pause
exit /b 1

:error
echo.
echo Bridge installation failed. Review the message above.
pause
exit /b 1
