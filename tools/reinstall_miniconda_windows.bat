@echo off
setlocal EnableExtensions
set "ERR=0"

echo [1/4] Removing previous Miniconda and user conda caches (no conda CLI used^)...
if exist "D:\miniconda" (
  rd /s /q "D:\miniconda"
  if exist "D:\miniconda" (
    echo ERROR: Could not remove D:\miniconda. Close apps using it ^(terminals, IDEs, Python^) and run this script again.
    set ERR=1
    goto :eof
  )
)
if exist "%USERPROFILE%\.conda" rd /s /q "%USERPROFILE%\.conda"
if exist "%LOCALAPPDATA%\conda" rd /s /q "%LOCALAPPDATA%\conda"

echo [2/4] Downloading latest Miniconda x86_64...
curl.exe -fSL -o "%TEMP%\Miniconda3-latest-Windows-x86_64.exe" "https://repo.anaconda.com/miniconda/Miniconda3-latest-Windows-x86_64.exe"
if errorlevel 1 (
  echo ERROR: curl download failed.
  set ERR=1
  goto :eof
)

echo [3/4] Silent install to D:\miniconda ^(adds conda to PATH for your user^)...
start /wait "" "%TEMP%\Miniconda3-latest-Windows-x86_64.exe" /InstallationType=JustMe /AddToPath=1 /RegisterPython=0 /S /D=D:\miniconda
if not exist "D:\miniconda\Scripts\conda.exe" (
  echo ERROR: Install finished but D:\miniconda\Scripts\conda.exe is missing.
  set ERR=1
  goto :eof
)

echo [4/4] conda init + explicit defaults channel...
"D:\miniconda\Scripts\conda.exe" config --add channels defaults 2>nul
"D:\miniconda\Scripts\conda.exe" init powershell
"D:\miniconda\Scripts\conda.exe" --version
"D:\miniconda\python.exe" -c "import sys; print('python', sys.version)"

echo.
echo Done. Open a NEW PowerShell window. From your repo clone root, recreate maintainer envs with conda, e.g.:
echo   conda env create -f environment.yml
echo   conda activate sts2-context-coach
echo   pip install ...
endlocal & exit /b %ERR%
