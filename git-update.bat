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

echo.
echo Current changes:
echo ------------------------------------------------------------
"%GIT_EXE%" status --short
echo ------------------------------------------------------------
echo.

set "HAS_CHANGES="
for /f "delims=" %%S in ('"%GIT_EXE%" status --porcelain') do set "HAS_CHANGES=1"
if defined HAS_CHANGES goto :ask_message

echo There are no local changes to commit.
goto :push

:ask_message
set "COMMIT_MESSAGE="
set /p "COMMIT_MESSAGE=Enter a commit message: "
if not defined COMMIT_MESSAGE (
    echo.
    echo A commit message is required. Nothing was published.
    pause
    exit /b 1
)

echo.
echo Staging project changes...
"%GIT_EXE%" add -A
if errorlevel 1 goto :error

echo.
echo Files being committed:
"%GIT_EXE%" diff --cached --stat
echo.
choice /c YN /n /m "Commit and publish these files? [Y/N]: "
if errorlevel 2 (
    echo.
    echo Cancelled. The files remain staged but were not committed or pushed.
    pause
    exit /b 0
)

"%GIT_EXE%" commit -m "%COMMIT_MESSAGE%"
if errorlevel 1 goto :error

:push
for /f "delims=" %%B in ('"%GIT_EXE%" branch --show-current') do set "CURRENT_BRANCH=%%B"
if not defined CURRENT_BRANCH (
    echo Unable to determine the current Git branch.
    goto :error
)

echo.
echo Pushing %CURRENT_BRANCH% to origin...
"%GIT_EXE%" push -u origin "%CURRENT_BRANCH%"
if errorlevel 1 goto :error

echo.
echo GitHub is up to date.
pause
exit /b 0

:error
echo.
echo The Git operation failed. Review the message above.
pause
exit /b 1
