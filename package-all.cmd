@echo off
setlocal
cd /d "%~dp0"

echo AIMaster one-click packager
echo Creating packages, updating the running copy, and opening the new version...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0package-and-restart.ps1" %*
set "PACKAGE_EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%PACKAGE_EXIT_CODE%"=="0" (
    echo Packaging failed. Review the error above.
) else (
    echo Packaging complete. Files are in: %~dp0release
)
echo.
pause
exit /b %PACKAGE_EXIT_CODE%
