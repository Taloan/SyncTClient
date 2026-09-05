; Das Installationspaket fuer SyncTClient.
;
; Uebersetzt wird es von tools\Veroeffentlichen.ps1; die Fassung kommt von
; dort und steht nicht hier, sonst gaebe es zwei Stellen mit derselben Zahl:
;
;   ISCC.exe /DFassung=1.2.3 /DQuelle=...\BIN /DZiel=...\dist setup\SyncTClient.iss

#ifndef Fassung
  #define Fassung "0.0.0"
#endif

#ifndef Quelle
  #define Quelle "..\BIN"
#endif

#ifndef Ziel
  #define Ziel "..\dist"
#endif

[Setup]
; Die Kennung bleibt ueber alle Fassungen dieselbe. An ihr erkennt Windows
; eine bestehende Installation und ersetzt sie, statt eine zweite anzulegen.
AppId={{0D208188-67C7-44F2-BDA7-2FFCAFD0F21B}
AppName=SyncTClient
AppVersion={#Fassung}
AppVerName=SyncTClient {#Fassung}
VersionInfoVersion={#Fassung}
AppPublisher=Dirk Mertens

; Ohne Administratorrechte.
;
; Das Programm traegt seine Shell-Erweiterung unter HKEY_CURRENT_USER ein und
; meldet seine Sync-Wurzeln fuer den angemeldeten Benutzer an. Eine
; Installation fuer alle Benutzer waere ein Versprechen, das es nicht
; einhalten kann.
PrivilegesRequired=lowest
DefaultDirName={autopf}\SyncTClient
DefaultGroupName=SyncTClient
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\SyncTClient.exe
UninstallDisplayName=SyncTClient {#Fassung}

; Nur auf 64-Bit-Windows. Die Erweiterung ist nativ fuer x64 gebaut, und der
; Platzhalter-Dienst von Windows gibt es unter 32 Bit ohnehin nicht.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

OutputDir={#Ziel}
OutputBaseFilename=SyncTClient-{#Fassung}-setup
SetupIconFile=..\src\SyncTClient.Gui\SyncTClient.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "deutsch"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
Name: "autostart"; Description: "Beim Anmelden starten"; Flags: unchecked

[Files]
; Das ganze Veroeffentlichungsverzeichnis. Es ist self-contained -- die
; Laufzeit liegt darin, auf dem Zielrechner wird nichts vorausgesetzt.
Source: "{#Quelle}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SyncTClient"; Filename: "{app}\SyncTClient.exe"
Name: "{group}\{cm:UninstallProgram,SyncTClient}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SyncTClient"; Filename: "{app}\SyncTClient.exe"; Tasks: desktopicon
Name: "{userstartup}\SyncTClient"; Filename: "{app}\SyncTClient.exe"; Tasks: autostart

[Run]
Filename: "{app}\SyncTClient.exe"; Description: "{cm:LaunchProgram,SyncTClient}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Was das Programm zur Laufzeit neben sich anlegt. Ohne diese Zeilen bleibt
; das Verzeichnis nach dem Entfernen stehen.
Type: filesandordirs; Name: "{app}\synct-home"

[Messages]
deutsch.WelcomeLabel2=Damit wird SyncTClient {#Fassung} auf diesem Rechner eingerichtet.%n%nDie Einbindung in den Explorer -- Vorschaubilder und Kontextmenü -- trägt das Programm beim ersten Start selbst ein. Administratorrechte werden dafür nicht gebraucht.
