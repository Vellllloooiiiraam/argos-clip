@echo off
REM ===========================================================================
REM  Argos Clip - gera o INSTALADOR (setup.exe) para distribuir.
REM  1) compila o app (dist\)
REM  2) garante o FFmpeg dentro de dist\tools\
REM  3) roda o Inno Setup e cria installer\out\ArgosClip-Setup-1.0.0.exe
REM ===========================================================================
setlocal
cd /d "%~dp0"

echo.
echo [1/3] Compilando o app...
call build.bat
if errorlevel 1 exit /b 1

echo.
echo [2/3] Conferindo o FFmpeg em dist\tools...
if exist "dist\tools\ffmpeg.exe" if exist "dist\tools\ffprobe.exe" goto :ffmpeg_ok
set "SRC_ffmpeg="
for %%P in (ffmpeg.exe) do set "SRC_ffmpeg=%%~$PATH:P"
set "SRC_ffprobe="
for %%P in (ffprobe.exe) do set "SRC_ffprobe=%%~$PATH:P"
if not defined SRC_ffmpeg (
  echo [ERRO] ffmpeg.exe nao encontrado no PATH.
  echo        Instale o FFmpeg ou copie ffmpeg.exe e ffprobe.exe para dist\tools\
  exit /b 1
)
copy /y "%SRC_ffmpeg%" "dist\tools\ffmpeg.exe" >nul
copy /y "%SRC_ffprobe%" "dist\tools\ffprobe.exe" >nul
:ffmpeg_ok

echo.
echo [3/3] Compilando o instalador (Inno Setup)...
REM nao empacotar dados de execucao que o app cria ao lado do exe
if exist "dist\jobs" rd /s /q "dist\jobs"
if exist "dist\models" rd /s /q "dist\models"
set "ISCC="
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC (
  echo [ERRO] Inno Setup nao encontrado.
  echo        Instale com:  winget install JRSoftware.InnoSetup
  exit /b 1
)

"%ISCC%" "installer\argos-clip.iss"
if errorlevel 1 (
  echo.
  echo [ERRO] Falha ao compilar o instalador.
  exit /b 1
)

echo.
echo Pronto! Instalador em: installer\out\ArgosClip-Setup-1.2.1.exe
endlocal
exit /b 0
