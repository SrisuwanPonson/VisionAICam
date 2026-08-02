[Setup]
AppName=VisionAICam
AppVersion=26.08.01.01
DefaultDirName=C:\ClearEngine\VisionAICam
DisableDirPage=yes
DefaultGroupName=VisionAICam
OutputDir=D:\Srisuwan\InstallerOutput
OutputBaseFilename=VisionAICam_Setup_Version 26.08.01.01
Compression=lzma
SolidCompression=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "pyenv"; Description: "Install Python Environment"
Name: "pyscripts"; Description: "Install Python Scripts"
Name: "setupfiles"; Description: "Install Setup Tools"
Name: "Models";Description: "Load models"

[Files]
; Runtime (ติดตั้งเสมอ)
Source: "C:\ClearEngine\VisionAICam\Runtime\*"; DestDir: "{app}\Runtime"; Flags: ignoreversion recursesubdirs createallsubdirs

; PythonEnv (เลือกได้)
Source: "C:\Program Files\Python313\*"; DestDir: "{app}\PythonEnv\Python313"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: pyenv

; PythonScripts (เลือกได้)
Source: "C:\ClearEngine\VisionAICam\PythonScripts\*"; DestDir: "{app}\PythonScripts"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: pyscripts

; Setup Tools (เลือกได้)
Source: "C:\ClearEngine\VisionAICam\Setup\*"; DestDir: "{app}\Setup"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: setupfiles

; Models
Source: "C:\ClearEngine\VisionAICam\Models\*"; DestDir: "{app}\Setup"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: Models


[Icons]
Name: "{group}\VisionAICam"; Filename: "{app}\Runtime\VisionAICam.exe"; WorkingDir: "{app}\Runtime"
Name: "{commondesktop}\VisionAICam"; Filename: "{app}\Runtime\VisionAICam.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Runtime\VisionAICam.exe"; Description: "Launch VisionAICam"; Flags: postinstall nowait skipifsilent unchecked

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
