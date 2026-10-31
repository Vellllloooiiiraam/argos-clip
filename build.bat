@echo off
title Argos Clip - Build do EXE
cd /d "%~dp0"
echo ============================================
echo  Gerando argos-clip.exe (pasta distribuivel)
echo ============================================
.venv\Scripts\pyinstaller.exe --noconfirm argos.spec
if errorlevel 1 (
    echo.
    echo FALHA no build - veja os erros acima.
    pause
    exit /b 1
)
echo.
echo OK: dist\argos-clip\argos-clip.exe
echo Para distribuir, compacte a pasta dist\argos-clip em um .zip
pause
