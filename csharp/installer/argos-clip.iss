; ===========================================================================
;  Argos Clip - instalador (Inno Setup 6)
;  Empacota a pasta ..\dist em um setup.exe com atalhos e desinstalador.
;  Compile com: build-installer.bat   (ou ISCC.exe installer\argos-clip.iss)
; ===========================================================================

#define AppName        "Argos Clip"
#define AppVersion     "1.2.4"
#define AppPublisher   "Argos Clip"
#define AppExeName     "argos-clip.exe"
#define DistDir        "..\dist"

[Setup]
; AppId identifica o app (NUNCA mude depois de publicar).
AppId={{8F3B2C1A-6D4E-4A9B-9C21-7E5A1B2D3C4F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Instala por-usuario (sem admin) em %LocalAppData%\Programs\Argos Clip,
; que e gravavel pelo app (ele cria jobs\ e models\ ao lado do exe).
PrivilegesRequired=lowest
OutputDir=out
OutputBaseFilename=ArgosClip-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Todo o conteudo publicado (exe, dlls, runtimes, wwwroot, tools\ffmpeg...)
; NUNCA empacotar dados de execucao (jobs\, models\) que o app cria ao lado do exe.
Source: "{#DistDir}\*"; DestDir: "{app}"; Excludes: "jobs\*,models\*,*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion
#if FileExists(AddBackslash(SourcePath) + "redist\MicrosoftEdgeWebview2Setup.exe")
; Bootstrapper do WebView2 (Evergreen): instalado SO se a runtime estiver faltando.
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall dontcopy
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
#if FileExists(AddBackslash(SourcePath) + "redist\MicrosoftEdgeWebview2Setup.exe")
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Instalando o WebView2 (so na primeira vez)..."; Flags: waituntilterminated; Check: NeedsWebView2
#endif

[UninstallDelete]
; limpa dados gerados pelo app (jobs/modelos) ao desinstalar
Type: filesandordirs; Name: "{app}\jobs"
Type: filesandordirs; Name: "{app}\models"

[Code]
{ A janela nativa usa o Edge WebView2. Ele ja vem no Windows 11 e no Windows 10
  atualizado; em maquinas antigas, instalamos a runtime pelo bootstrapper. }
function NeedsWebView2: Boolean;
var
  V: String;
begin
  Result := True;
  if RegQueryStringValue(HKLM32,
       'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V)
     and (V <> '') and (V <> '0.0.0.0') then Result := False
  else if RegQueryStringValue(HKLM,
       'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V)
     and (V <> '') and (V <> '0.0.0.0') then Result := False
  else if RegQueryStringValue(HKCU,
       'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V)
     and (V <> '') and (V <> '0.0.0.0') then Result := False;
end;
