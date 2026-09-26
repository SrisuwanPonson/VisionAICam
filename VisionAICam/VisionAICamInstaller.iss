[Setup]
AppName=VisionAICam
AppVersion=26.08.30
DefaultDirName=C:\ClearEngine\VisionAICam
DisableDirPage=yes
; ปรับมาใช้คำสั่งมาตรฐานในการจัดการ Start Menu ร่วมกับ commonprograms
DefaultGroupName=VisionAICam
DisableProgramGroupPage=yes
OutputDir=D:\Srisuwan\InstallerOutput
OutputBaseFilename=VisionAICam_Setup_26.08.30
Compression=lzma
SolidCompression=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
; จัดกลุ่มส่วนระบบหลัก (เลือกเป็นค่าเริ่มต้น)
Name: "pyenv"; Description: "Install Python Environment"; GroupDescription: "Core Components:"
Name: "pyscripts"; Description: "Install Python Scripts"; GroupDescription: "Core Components:"
Name: "setupfiles"; Description: "Install Setup Tools"; GroupDescription: "Core Components:"
Name: "models"; Description: "Load Models"; GroupDescription: "Core Components:"
; จัดกลุ่มส่วน Demo (ปิดไว้เป็นค่าเริ่มต้นเพื่อไม่ให้ไฟล์หนักเครื่องโดยไม่จำเป็น)
Name: "demo_datasets"; Description: "Include Datasets for demo"; GroupDescription: "Demo Materials:"; Flags: unchecked
Name: "demo_images"; Description: "Include Images for demo"; GroupDescription: "Demo Materials:"; Flags: unchecked
Name: "demo_model"; Description: "Include Model for demo"; GroupDescription: "Demo Materials:"; Flags: unchecked

[Files]
Source: "C:\ClearEngine\VisionAICam\Runtime\*"; DestDir: "{app}\Runtime"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "C:\ClearEngine\VisionAICam\PythonEnv\*"; DestDir: "{app}\PythonEnv"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: pyenv
Source: "C:\ClearEngine\VisionAICam\PythonScripts\*"; DestDir: "{app}\PythonScripts"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: pyscripts
Source: "C:\ClearEngine\VisionAICam\Setup\*"; DestDir: "{app}\Setup"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: setupfiles
Source: "C:\ClearEngine\VisionAICam\Models\*"; DestDir: "{app}\Models"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: models
Source: "C:\ClearEngine\VisionAICam\Datasets\*"; DestDir: "{app}\Datasets"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: demo_datasets
Source: "C:\ClearEngine\VisionAICam\Images\*"; DestDir: "{app}\Images"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: demo_images
Source: "C:\ClearEngine\VisionAICam\Model\*"; DestDir: "{app}\Model"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: demo_model

[Icons]
; แก้ไขจาก {group} เป็น {commonprograms} เพื่อให้สร้าง Shortcut ใน Start Menu ได้โดยไม่ติด Error
Name: "{commonprograms}\VisionAICam"; Filename: "{app}\Runtime\VisionAICam.exe"; WorkingDir: "{app}\Runtime"
Name: "{commondesktop}\VisionAICam"; Filename: "{app}\Runtime\VisionAICam.exe"; WorkingDir: "{app}\Runtime"; Tasks: desktopicon

[Run]
Filename: "{app}\Runtime\VisionAICam.exe"; Description: "Launch VisionAICam"; Flags: postinstall nowait skipifsilent unchecked

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;