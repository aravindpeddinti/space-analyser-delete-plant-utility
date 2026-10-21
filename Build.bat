@echo off
title Build Space Analyzer
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework 4 compiler on this computer.
  echo Install ".NET Framework 4.8" from Microsoft, then run this file again.
  pause
  exit /b 1
)

echo Building SpaceAnalyzer.exe ...
echo.
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:SpaceAnalyzer.exe /win32icon:app.ico /codepage:65001 /win32manifest:app.manifest /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.DirectoryServices.dll /r:System.Management.dll SpaceAnalyzer.cs

if errorlevel 1 (
  echo.
  echo BUILD FAILED. Copy the error text above and send it to Claude.
  pause
  exit /b 1
)

echo.
echo SUCCESS! SpaceAnalyzer.exe is ready in this folder.
echo Copy SpaceAnalyzer.exe to any Windows VM and double-click it.
pause
