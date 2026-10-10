@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title StressDeez V10 - Build
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "SDK="
rem Prefer an existing .NET SDK. Reuse old TwinBurn SDK downloads if present.
where dotnet.exe >nul 2>&1
if not errorlevel 1 (
    dotnet --list-sdks | findstr /b /c:"8." /c:"9." /c:"10." >nul 2>&1
    if not errorlevel 1 set "SDK=dotnet"
)
if not defined SDK if exist "%LOCALAPPDATA%\TwinBurn\dotnet\dotnet.exe" set "SDK=%LOCALAPPDATA%\TwinBurn\dotnet\dotnet.exe"
if not defined SDK if exist "%LOCALAPPDATA%\StressDeez\dotnet\dotnet.exe" set "SDK=%LOCALAPPDATA%\StressDeez\dotnet\dotnet.exe"
if not defined SDK if exist "%CD%\.build-tools\dotnet\dotnet.exe" set "SDK=%CD%\.build-tools\dotnet\dotnet.exe"
if not defined SDK (
    echo SDK not found. Downloading .NET 8 once to a shared user cache...
    if not exist "%LOCALAPPDATA%\StressDeez" mkdir "%LOCALAPPDATA%\StressDeez"
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile "$env:LOCALAPPDATA\StressDeez\dotnet-install.ps1"; & "$env:LOCALAPPDATA\StressDeez\dotnet-install.ps1" -Channel 8.0 -InstallDir "$env:LOCALAPPDATA\StressDeez\dotnet" -NoPath"
    if errorlevel 1 goto failure
    set "SDK=%LOCALAPPDATA%\StressDeez\dotnet\dotnet.exe"
)
if not exist "dist" mkdir "dist"
echo Building StressDeez portable Windows x64 EXE...
"%SDK%" publish "StressDeez.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "%CD%\dist" -nologo -v:minimal >"%CD%\build.log" 2>&1
set "RESULT=%ERRORLEVEL%"
type "build.log"
if not "%RESULT%"=="0" goto failure
if not exist "dist\StressDeez.exe" goto failure
echo.
echo SUCCESS: %CD%\dist\StressDeez.exe
echo.
pause
exit /b 0
:failure
echo.
echo BUILD FAILED. Send the first error from build.log for a targeted fix.
pause
exit /b 1
