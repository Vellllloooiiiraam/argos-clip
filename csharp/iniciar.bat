@echo off
REM ===========================================================================
REM  Argos Clip (C#) - abre o app (janela nativa).
REM  Se dist\argos-clip.exe existir, usa ele; senao roda em modo dev (dotnet run).
REM ===========================================================================
setlocal
cd /d "%~dp0"

echo.
echo  ==============================================
echo   Argos Clip - 1 video longo -^> N clipes virais
echo  ==============================================
echo.
echo  A janela do app vai abrir sozinha.
echo  Feche a janela do app para encerrar.
echo.

if exist "dist\argos-clip.exe" (
  "dist\argos-clip.exe"
  exit /b %ERRORLEVEL%
)

echo  [info] dist\argos-clip.exe nao existe ainda; rodando em modo dev.
echo  [info] Rode "build.bat" para gerar o executavel.
echo.

call :find_dotnet
if not defined DOTNET (
  echo [ERRO] .NET SDK nao encontrado. Instale o .NET SDK 8.
  exit /b 1
)
"%DOTNET%" run --project "src\ArgosClip\ArgosClip.csproj" -c Release
endlocal
exit /b 0

:find_dotnet
set "DOTNET="
for %%D in (dotnet.exe) do if not defined DOTNET set "DOTNET=%%~$PATH:D"
if not defined DOTNET if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles(x86)%\dotnet\dotnet.exe"
exit /b 0
