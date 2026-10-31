@echo off
REM ===========================================================================
REM  Argos Clip (C#) - teste rapido de fumaca (smoke test).
REM  Gera um video falado com a voz do Windows, sobe o app numa porta de
REM  teste, envia o video e confere se os clipes foram gerados.
REM ===========================================================================
setlocal
cd /d "%~dp0"

call :find_dotnet
if not defined DOTNET (
  echo [ERRO] .NET SDK nao encontrado. Instale o .NET SDK 8.
  exit /b 1
)

set "TESTDIR=%TEMP%\argos-clip-test"
set "ARGOS_PORT=8599"
set "ARGOS_WHISPER_MODEL=tiny"
set "ARGOS_JOBS_DIR=%TESTDIR%\jobs"

echo.
echo [1/3] Gerando video de teste com fala (voz do Windows)...
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\make-test-video.ps1" -OutDir "%TESTDIR%"
if errorlevel 1 (
  echo [ERRO] Falha ao gerar o video de teste.
  exit /b 1
)

echo.
echo [2/3] Compilando...
"%DOTNET%" build "src\ArgosClip\ArgosClip.csproj" -c Debug >nul
if errorlevel 1 (
  echo [ERRO] A compilacao falhou.
  exit /b 1
)

echo.
echo [3/3] Rodando o teste de ponta a ponta (pode baixar o modelo Whisper "tiny")...
echo       Job em: %ARGOS_JOBS_DIR%
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\run-smoke-test.ps1" -Video "%TESTDIR%\test.mp4" -Port %ARGOS_PORT% -Dotnet "%DOTNET%" -Dll "src\ArgosClip\bin\Debug\net8.0-windows\argos-clip.dll"

endlocal
exit /b 0

:find_dotnet
set "DOTNET="
for %%D in (dotnet.exe) do if not defined DOTNET set "DOTNET=%%~$PATH:D"
if not defined DOTNET if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles(x86)%\dotnet\dotnet.exe"
exit /b 0
