@echo off
REM Builds a lightweight, self-contained dist\m4xmudovv-Setup.exe
REM Needs on this PC: .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)
REM                    Inno Setup 6   (https://jrsoftware.org/isdl.php)
setlocal
cd /d "%~dp0"

if exist publish rmdir /s /q publish
if exist dist rmdir /s /q dist
dotnet publish src\m4xmudovv\m4xmudovv.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o publish
if errorlevel 1 goto :fail

set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php and run this file again.
  goto :fail
)
"%ISCC%" installer\m4xmudovv.iss
if errorlevel 1 goto :fail

echo.
echo DONE: dist\m4xmudovv-Setup.exe
pause
exit /b 0

:fail
echo.
echo BUILD FAILED. Send me the text above.
pause
exit /b 1
