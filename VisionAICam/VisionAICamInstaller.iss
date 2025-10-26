[Setup]
AppName=VisionAICam
AppVersion=1.0
DefaultDirName={pf}\VisionAICam
DefaultGroupName=VisionAICam
OutputDir=D:\Srisuwan\InstallerOutput
OutputBaseFilename=VisionAICamInstaller
Compression=lzma
SolidCompression=yes

[Files]
Source: "D:\Srisuwan\AI_Project\VisionAICam\VisionAICam\bin\Debug\net8.0-windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "D:\Srisuwan\AI_Project\VisionAICam\VisionAICam\bin\Debug\net8.0-windows\Dataset\*"; DestDir: "{app}\Dataset"; Flags: skip
Source: "D:\Srisuwan\AI_Project\VisionAICam\VisionAICam\bin\Debug\net8.0-windows\Images\*"; DestDir: "{app}\Images"; Flags: skip
Source: "D:\Srisuwan\AI_Project\VisionAICam\VisionAICam\bin\Debug\net8.0-windows\log\*"; DestDir: "{app}\log"; Flags: skip
Source: "D:\Srisuwan\AI_Project\VisionAICam\VisionAICam\bin\Debug\net8.0-windows\runs\*"; DestDir: "{app}\runs"; Flags: skip

[Registry]
Root: HKCU; Subkey: "Software\VisionAICam"; ValueType: string; ValueName: "InstallDate"; ValueData: "{code:GetDate}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\VisionAICam"; ValueType: string; ValueName: "TrialUsed"; ValueData: "true"; Flags: uninsdeletekey

[Icons]
Name: "{group}\VisionAICam"; Filename: "{app}\VisionAICam.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\VisionAICam.exe"; Description: "Launch VisionAICam"; Flags: postinstall nowait skipifsilent unchecked

[Code]
function GetDate(Value: string): string;
begin
  Result := GetDateTimeString('yyyy-mm-dd', '-', ':');
end;

function InitializeSetup(): Boolean;
var
  trialUsed: string;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER, 'Software\VisionAICam', 'TrialUsed', trialUsed) then
  begin
    if trialUsed = 'true' then
    begin
      MsgBox('Trial already used. Please enter a license key to reinstall.', mbError, MB_OK);
      Result := False; // Cancel installation
      exit;
    end;
  end;
  Result := True; // Allow install
end;