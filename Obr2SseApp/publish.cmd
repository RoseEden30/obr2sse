@echo off
rem Publishes one self-contained Obr2Sse.exe. Runtime, native library and texconv are bundled, so it
rem runs on a machine with no .NET installed and nothing beside it.
cd /d "%~dp0"

dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:IncludeAllContentForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none
if errorlevel 1 goto :eof

set OUT=bin\Release\net10.0-windows\win-x64\publish
move /y "%OUT%\Obr2SseApp.exe" "%OUT%\Obr2Sse.exe" >nul

echo.
echo Output: %CD%\%OUT%\Obr2Sse.exe
pause
