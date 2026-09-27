@echo off
cd /d "%~dp0"
echo Fermeture de PaletteMap si elle tourne...
taskkill /IM PaletteMap.exe /F >nul 2>&1
echo Compilation...
dotnet publish -c Release -o publish
if errorlevel 1 (
  echo.
  echo ECHEC de la compilation : envoyer une capture du message ci-dessus.
  pause
  exit /b 1
)
echo.
echo OK : l executable est dans le dossier publish\PaletteMap.exe
pause
