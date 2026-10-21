@echo off
setlocal
cd /d "%~dp0"
echo Building the combined Space Analyzer and SEM plant helper...

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo ERROR: .NET Framework 4 compiler was not found.
  exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:SpaceAnalyzer.exe /win32icon:app.ico /codepage:65001 /win32manifest:app.manifest /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.DirectoryServices.dll /r:System.Management.dll SpaceAnalyzer.cs
if errorlevel 1 exit /b 1

set "PY="
if exist ".venv\Scripts\python.exe" set "PY=.venv\Scripts\python.exe"
if not defined PY (where py >nul 2>nul && set "PY=py -3")
if not defined PY (where python >nul 2>nul && set "PY=python")
if not defined PY (
  echo ERROR: Python was not found. Install Python 3.10+ and the packages in requirements.txt.
  exit /b 1
)

%PY% -m PyInstaller --version >nul 2>nul
if errorlevel 1 (
  echo ERROR: PyInstaller is missing. Run: %PY% -m pip install -r requirements.txt
  exit /b 1
)

%PY% -m PyInstaller --noconfirm --clean --onefile --console --name "Delete Plant Utility CLI" --collect-all oracledb --collect-all cryptography --hidden-import=pyodbc --hidden-import=cryptography.x509 --hidden-import=secrets delete_plant_cli.py
if errorlevel 1 exit /b 1
copy /y "dist\Delete Plant Utility CLI.exe" "Delete Plant Utility CLI.exe" >nul
if errorlevel 1 exit /b 1

echo.
echo Build complete:
echo   SpaceAnalyzer.exe
echo   Delete Plant Utility CLI.exe
echo Keep both files beside each other. The SEM plant feature needs the CLI helper.
endlocal
