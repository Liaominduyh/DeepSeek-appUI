@echo off
rem Build dsh-appui npm package: dotnet publish -> packaging/app, then npm pack
setlocal
cd /d "%~dp0"

if exist packaging\app rmdir /s /q packaging\app
dotnet publish -c Release -o packaging\app
if errorlevel 1 goto :error

cd packaging
call npm pack
if errorlevel 1 goto :error
cd ..

echo Build OK: packaging\dsh-appui-*.tgz
exit /b 0

:error
cd /d "%~dp0"
echo Build failed
exit /b 1
