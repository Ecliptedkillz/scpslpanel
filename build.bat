@echo off
setlocal

cd /d "%~dp0"

echo.
echo [1/2] Building the SCP Control web dashboard...
pushd "src\scpsl-panel-web"

if not exist "node_modules\" (
    echo Installing web dependencies...
    call npm.cmd install --no-audit --no-fund
    if errorlevel 1 goto :error
)

call npm.cmd run build
if errorlevel 1 goto :error
popd

echo.
echo [2/2] Building the SCP Control API...
dotnet build "ScpSlPanel.sln"
if errorlevel 1 goto :error

echo.
echo Build completed successfully.
exit /b 0

:error
set "BUILD_EXIT_CODE=%errorlevel%"
popd 2>nul
echo.
echo Build failed with exit code %BUILD_EXIT_CODE%.
exit /b %BUILD_EXIT_CODE%
