@echo off
setlocal
cd /d "%~dp0"
echo Delete Plant Utility - build (INTERNAL USE ONLY)
echo Folder: %CD%
echo.
rem Usage:  build_exe.bat            fast-starting folder build (recommended)
rem         build_exe.bat onefile    single .exe (slower to start: it unpacks itself on every launch)

set "MODE=--onedir"
if /i "%~1"=="onefile" set "MODE=--onefile"

rem Prefer the project venv, then the py launcher, then python on PATH
set "PY="
if exist ".venv\Scripts\python.exe" set "PY=.venv\Scripts\python.exe"
if not defined PY ( where py >nul 2>nul && set "PY=py -3" )
if not defined PY ( where python >nul 2>nul && set "PY=python" )
if not defined PY (
  echo ERROR: Python was not found. Install Python 3.10+ (32-bit or 64-bit) and try again.
  goto :end
)
echo Using: %PY%
%PY% -c "import struct,sys;print('Python',sys.version.split()[0],str(struct.calcsize('P')*8)+'-bit')"

%PY% -m PyInstaller --version >nul 2>nul
if errorlevel 1 (
  echo.
  echo PyInstaller is not installed for this Python. Run:
  echo     %PY% -m pip install -r requirements.txt
  goto :end
)

set "ICON="
if exist "assets\app.ico" set "ICON=--icon=assets\app.ico"

echo.
echo Building (%MODE%)...
%PY% -m PyInstaller --noconfirm --clean %MODE% --windowed --name "Delete Plant Utility" %ICON% --add-data "assets\app.ico;assets" --add-data "assets\app.png;assets" --version-file=version.txt --collect-all PyQt5 --collect-all oracledb --collect-all cryptography --hidden-import=pyodbc --hidden-import=cryptography.x509 --hidden-import=secrets --hidden-import=asyncio --hidden-import=uuid --hidden-import=PyQt5.QtCore --hidden-import=PyQt5.QtGui --hidden-import=PyQt5.QtWidgets --exclude-module=tkinter --exclude-module=unittest main.py
if errorlevel 1 (
  echo.
  echo BUILD FAILED - see the messages above.
  goto :end
)
echo.
if /i "%MODE%"=="--onefile" (
  echo Done: dist\Delete Plant Utility.exe
) else (
  echo Done: dist\Delete Plant Utility\Delete Plant Utility.exe
  echo Copy the whole "Delete Plant Utility" folder to the target machine.
)

echo.
echo Building the console helper used by Space Analyzer...
%PY% -m PyInstaller --noconfirm --clean --onefile --console --name "Delete Plant Utility CLI" --collect-all oracledb --collect-all cryptography --hidden-import=pyodbc --hidden-import=cryptography.x509 --hidden-import=secrets delete_plant_cli.py
if errorlevel 1 (
  echo.
  echo CLI helper build failed - see the messages above.
  goto :end
)
echo Done: dist\Delete Plant Utility CLI.exe
echo Keep this helper beside SpaceAnalyzer.exe when packaging the combined app.

:end
echo.
pause
endlocal
