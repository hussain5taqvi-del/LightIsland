@echo off
cd /d "%~dp0"
echo Building LightIsland.exe, please wait...
echo (.NET 8 SDK and internet are needed ONLY on this PC, only for building.)
echo.
taskkill /f /im LightIsland.exe >nul 2>nul
if exist release rmdir /s /q release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o release
if errorlevel 1 (
    echo.
    echo Build failed. Send the error above to get it fixed.
    pause
    exit /b 1
)
for /f %%H in ('certutil -hashfile "release\LightIsland.exe" SHA256 ^| findstr /r /x "[0-9a-fA-F][0-9a-fA-F]*"') do echo %%H  LightIsland.exe > release\SHA256.txt
echo.
echo Done! One file: %cd%\release\LightIsland.exe
echo Copy it to any PC and double click. No internet, no .NET, nothing else needed.
echo.
pause
