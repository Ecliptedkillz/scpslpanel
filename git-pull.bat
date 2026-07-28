@echo off
setlocal

cd /d "%~dp0"

set "GIT_EXE="
for /f "delims=" %%G in ('where git.exe 2^>nul') do if not defined GIT_EXE set "GIT_EXE=%%G"
if not defined GIT_EXE if exist "C:\Program Files\Git\cmd\git.exe" set "GIT_EXE=C:\Program Files\Git\cmd\git.exe"

if not defined GIT_EXE (
    echo.
    echo Git could not be found. Install Git for Windows, then reopen this script.
    echo Checked folder: %CD%
    pause
    exit /b 1
)

if not exist "%~dp0.git" (
    echo.
    echo This copy does not contain the hidden .git folder.
    echo Checked folder: %CD%
    echo.
    echo Run this script from your cloned repository, not from a ZIP or copied folder.
    pause
    exit /b 1
)

set "GIT_CONFIG_COUNT=1"
set "GIT_CONFIG_KEY_0=safe.directory"
set "GIT_CONFIG_VALUE_0=%CD%"

"%GIT_EXE%" rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo.
    echo Git could not open the repository.
    echo Checked folder: %CD%
    pause
    exit /b 1
)

for /f "delims=" %%S in ('"%GIT_EXE%" status --porcelain') do (
    echo.
    echo Pull cancelled because local changes are present:
    "%GIT_EXE%" status --short
    echo.
    echo Run git-update.bat first, or handle the local changes manually.
    pause
    exit /b 1
)

for /f "delims=" %%B in ('"%GIT_EXE%" branch --show-current') do set "CURRENT_BRANCH=%%B"
if not defined CURRENT_BRANCH (
    echo.
    echo Unable to determine the current Git branch.
    pause
    exit /b 1
)

echo.
echo Pulling origin/%CURRENT_BRANCH%...
"%GIT_EXE%" pull --ff-only origin "%CURRENT_BRANCH%"
if errorlevel 1 (
    echo.
    echo Pull failed. No automatic merge was attempted.
    pause
    exit /b 1
)

echo.
echo Repository updated successfully.
echo Run build.bat if application files changed.
pause
exit /b 0
