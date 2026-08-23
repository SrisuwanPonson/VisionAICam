[Setup]
AppName=VisionAICam
AppVersion=26.08.01.01
DefaultDirName=C:\ClearEngine\VisionAICam
DisableDirPage=yes
DefaultGroupName=VisionAICam
OutputDir=D:\Srisuwan\InstallerOutput
OutputBaseFilename=VisionAICam_Setup_26.08.01.01
Compression=lzma
SolidCompression=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "pyenv"; Description: "Install Python Environment"
Name: "pyscripts"; Description: "Install Python Scripts"
Name: "setupfiles"; Description: "Install Setup Tools"
Name: "models"; Description: "Load Models"

; Demo content
Name: "demo_datasets"; Description: "Include Datasets for demo"
Name: "demo_images"; Description: "Include Images for demo"
Name: "demo_model"; Description: "Include Model for demo"

[Files]
; Runtime (always installed)
Source: "C:\ClearEngine\VisionAICam\Runtime\*"; 
DestDir: "{app}\Runtime"; 
Flags: ignoreversion recursesubdirs createallsubdirs

; PythonEnv (optional)
Source: "C:\ClearEngine\VisionAICam\PythonEnv\*"; 
DestDir: "{app}\PythonEnv"; 
Flags: ignoreversion recursesubdirs createallsubdirs; 
Tasks: pyenv

; PythonScripts (optional)
Source: "C:\ClearEngine\VisionAICam\PythonScripts\*"; 
DestDir: "{app}\PythonScripts"; 
Flags: ignoreversion recursesubdirs createallsubdirs; 
Tasks: pyscripts

; Setup Tools (optional)
Source: "C:\ClearEngine\VisionAICam\Setup\*"; 
DestDir: "{app}\Setup"; 
Flags: ignoreversion recursesubdirs createallsubdirs; 
Tasks: setupfiles

; Models (optional)
Source: "C:\ClearEngine\VisionAICam\Models\*"; 
DestDir: "{app}\Models"; 
Flags: ignoreversion recursesubdirs createallsubdirs; 
Tasks: models

; Datasets (demo)
Source: "C:\ClearEngine\VisionAICam\Datasets\*";
DestDir: "{app}\Datasets";
Flags: ignoreversion recursesubdirs createallsubdirs;
Tasks: demo_datasets

; Images (demo)
Source: "C:\ClearEngine\VisionAICam\Images\*";
DestDir: "{app}\Images";
Flags: ignoreversion recursesubdirs createallsubdirs;
Tasks: demo_images

; Model (demo)
Source: "C:\ClearEngine\VisionAICam\Model\*";
DestDir: "{app}\Model";
Flags: ignoreversion recursesubdirs createallsubdirs;
Tasks: demo_model

[Icons]
Name: "{group}\VisionAICam"
Filename: "{app}\Runtime\VisionAICam.exe"
WorkingDir: "{app}\Runtime"

Name: "{commondesktop}\VisionAICam"
Filename: "{app}\Runtime\VisionAICam.exe"
Tasks: desktopicon

[Run]
Filename: "{app}\Runtime\VisionAICam.exe"
Description: "Launch VisionAICam"
Flags: postinstall nowait skipifsilent unchecked

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
