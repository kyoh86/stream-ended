#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId=StreamEnded
AppName=Stream Ended
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\StreamEnded
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\StreamEnded.exe
OutputDir=..\publish
OutputBaseFilename=StreamEnded-Setup-x64
Compression=lzma2
SolidCompression=yes
AppMutex=Local\StreamEnded

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
japanese.AutoStart=Windows にサインインしたときに Stream Ended を起動する
english.AutoStart=Start Stream Ended when I sign in to Windows

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"

[Files]
Source: "..\publish\StreamEnded.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Stream Ended"; Filename: "{app}\StreamEnded.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "StreamEnded"; ValueData: """{app}\StreamEnded.exe"" --background"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "StreamEnded"; Tasks: not autostart; Flags: deletevalue

[Run]
Filename: "{app}\StreamEnded.exe"; Description: "{cm:LaunchProgram,Stream Ended}"; Flags: nowait postinstall skipifsilent
