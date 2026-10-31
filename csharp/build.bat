@echo off
REM ===========================================================================
REM  Argos Clip (C#) - compila o executavel self-contained para Windows x64.
REM  Saida: dist\argos-clip.exe  (nao precisa de .NET instalado na maquina).
REM ===========================================================================
setlocal
cd /d "%~dp0"

call :find_dotnet
if not defined DOTNET (
  echo [ERRO] .NET SDK nao encontrado.
  echo         Instale o .NET SDK 8 em https://dotnet.microsoft.com/download
  exit /b 1
)
echo Usando: %DOTNET%

echo.
echo [1/2] Restaurando e compilando (Release)...
"%DOTNET%" publish "src\ArgosClip\ArgosClip.csproj" -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -o "dist"
if errorlevel 1 (
  echo.
  echo [ERRO] A compilacao falhou.
  exit /b 1
)

echo.
echo [2/2] Pronto! Executavel em: dist\argos-clip.exe
echo      Rode "iniciar.bat" para abrir o app.
endlocal
exit /b 0

:find_dotnet
set "DOTNET="
for %%D in (dotnet.exe) do if not defined DOTNET set "DOTNET=%%~$PATH:D"
if not defined DOTNET if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles(x86)%\dotnet\dotnet.exe"
exit /b 0
