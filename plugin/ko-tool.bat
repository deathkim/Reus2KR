@echo off
setlocal
set PYTHONIOENCODING=utf-8
set "TOOL=%~dp0tools\ko-tool.py"
set "WORKFILE=%~dp0translation\ko-work.jsonl"

where python >nul 2>nul
if errorlevel 1 (
  echo Python not found. Install Python or add it to PATH.
  pause
  exit /b 1
)

:menu
echo.
echo ===== Reus 2 translation tool  [translation\ko-work.jsonl] =====
echo  1. status           - pending edits / drift / conflicts
echo  2. diff             - list pending edits
echo  3. import dry-run   - preview what would be applied
echo  4. import           - apply pending edits to ko.csv and ko_current.csv
echo  5. sync             - merge ko.csv changes made elsewhere ^(e.g. by Claude^)
echo  6. export           - rebuild the working file from ko.csv
echo  7. verify           - round-trip check of ko.csv
echo  8. open working file in Notepad
echo  9. run validation   - run the validation script
echo  0. quit
set "choice="
set /p "choice=Select number: " || exit /b 0
if "%choice%"=="1" python "%TOOL%" status
if "%choice%"=="2" python "%TOOL%" diff --limit 60
if "%choice%"=="3" python "%TOOL%" import --dry-run
if "%choice%"=="4" python "%TOOL%" import
if "%choice%"=="5" python "%TOOL%" sync
if "%choice%"=="6" goto doexport
if "%choice%"=="7" python "%TOOL%" verify
if "%choice%"=="8" start "" notepad "%WORKFILE%"
if "%choice%"=="9" goto dovalidate
if "%choice%"=="0" exit /b 0
goto menu

:doexport
python "%TOOL%" export
if errorlevel 1 (
  echo.
  echo Export stopped because there are pending edits. Use 4 first, or 5 to merge.
  set "force="
  set /p "force=Overwrite and discard pending edits? [y/N]: " || goto menu
  if /i "%force%"=="y" python "%TOOL%" export --force
)
goto menu

:dovalidate
for %%F in ("%~dp0*.ps1") do powershell -NoProfile -ExecutionPolicy Bypass -File "%%F"
goto menu
