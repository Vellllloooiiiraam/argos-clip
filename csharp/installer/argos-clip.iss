; ===========================================================================
;  Argos Clip - instalador (Inno Setup 6)
;  Empacota a pasta ..\dist em um setup.exe com atalhos e desinstalador.
;  Compile com: build-installer.bat   (ou ISCC.exe installer\argos-clip.iss)
; ===========================================================================

#define AppName        "Argos Clip"
#define AppVersion     "1.2.0"
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

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; limpa dados gerados pelo app (jobs/modelos) ao desinstalar
Type: filesandordirs; Name: "{app}\jobs"
Type: filesandordirs; Name: "{app}\models"
