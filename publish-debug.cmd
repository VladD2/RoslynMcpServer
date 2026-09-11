@echo off
setlocal
cd /d "%~dp0"

rem Prefer the 64-bit dotnet host: PATH may prefer "Program Files (x86)\dotnet".
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

echo dotnet host: %DOTNET%

rem A running instance locks bin\Debug\net10.0\RoslynMcpServer.exe and its DLLs.
rem Stop all instances so the build can overwrite the output.
tasklist /FI "IMAGENAME eq RoslynMcpServer.exe" 2>nul | find /I "RoslynMcpServer.exe" >nul
if not errorlevel 1 (
    echo Stopping running RoslynMcpServer.exe processes...
    taskkill /F /IM RoslynMcpServer.exe >nul
)

echo.
echo Building Debug, output: bin\Debug\net10.0 ...
"%DOTNET%" build RoslynMcpServer.csproj -c Debug
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)

echo.
echo OK: bin\Debug\net10.0\RoslynMcpServer.exe
for %%F in ("bin\Debug\net10.0\RoslynMcpServer.exe") do echo Last write: %%~tF
echo Reload MCP in opencode to pick up the new binary.
endlocal
