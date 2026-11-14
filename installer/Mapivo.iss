#define AppName "Mapivo"

#define AppVersion "1.0.1.1"

#define AppPublisher "Mapivo"

#define AppExe "Mapivo.exe"



[Setup]

AppId={{8F3B2C74-6D1E-4A9B-9C55-2E7A1D4B6F31}

AppName={#AppName}

AppVersion={#AppVersion}

AppVerName={#AppName} {#AppVersion}

AppPublisher={#AppPublisher}

DefaultDirName={localappdata}\Programs\{#AppName}

DefaultGroupName={#AppName}

UninstallDisplayIcon={app}\{#AppExe}

UninstallDisplayName={#AppName}

Compression=lzma2/ultra64

SolidCompression=yes

WizardStyle=modern

SetupIconFile=..\src\Mapivo.App\mapivo.ico

OutputDir=..\out\installer

OutputBaseFilename=Mapivo-Setup-{#AppVersion}

ArchitecturesInstallIn64BitMode=x64compatible or arm64

ArchitecturesAllowed=x64compatible arm64

PrivilegesRequired=lowest

MinVersion=10.0

ShowLanguageDialog=yes

UsePreviousLanguage=no



[Languages]

Name: "english"; MessagesFile: "compiler:Default.isl"

Name: "farsi"; MessagesFile: "Farsi.isl"



[Tasks]

Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"



[InstallDelete]

Type: filesandordirs; Name: "{app}\DLL"

Type: filesandordirs; Name: "{app}\web"

Type: filesandordirs; Name: "{app}\runtimes"



[Files]

Source: "..\out\publish\x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs solidbreak; Check: PreferX64Files

Source: "..\out\publish\arm64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: PreferArm64Files



[Icons]

Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"

Name: "{autoprograms}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon



[Run]

Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent



[UninstallDelete]

Type: filesandordirs; Name: "{app}"



[CustomMessages]

english.UninstallDoneTitle=Uninstall complete

english.UninstallDoneText=Mapivo has been removed from this computer.

farsi.UninstallDoneTitle=حذف کامل شد

farsi.UninstallDoneText=مپایو یکسره از این کامپیوتر حذف شد.



[Code]

function PreferArm64Files: Boolean;

begin

  Result := IsArm64;

end;



function PreferX64Files: Boolean;

begin

  Result := not PreferArm64Files;

end;



const

  WebView2Key = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

  WebView2KeyUser = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';



function IsWebView2Installed(): Boolean;

var

  pv: String;

begin

  Result := False;

  if RegQueryStringValue(HKLM, WebView2Key, 'pv', pv) and (pv <> '') and (pv <> '0.0.0.0') then

    Result := True

  else if RegQueryStringValue(HKCU, WebView2KeyUser, 'pv', pv) and (pv <> '') and (pv <> '0.0.0.0') then

    Result := True;

end;



function InstallWebView2(): Boolean;

var

  InstallerPath: String;

  ResultCode: Integer;

begin

  Result := False;

  InstallerPath := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');



  try

    DownloadTemporaryFile(WebView2Url, 'MicrosoftEdgeWebview2Setup.exe', '', nil);

  except

    InstallerPath := '';

  end;



  if InstallerPath = '' then

    Exit;



  if not FileExists(InstallerPath) then

    Exit;



  if not Exec(InstallerPath, '/silent /install', '', SW_HIDE,

              ewWaitUntilTerminated, ResultCode) then

    Exit;



  Result := IsWebView2Installed();

end;



function PrepareToInstall(var NeedsRestart: Boolean): String;

begin

  Result := '';



  if IsWebView2Installed() then

    Exit;



  WizardForm.StatusLabel.Caption :=

    'Installing Microsoft WebView2 Runtime...';

  WizardForm.Refresh;



  if not InstallWebView2() then

  begin

    if MsgBox('Microsoft WebView2 Runtime could not be installed automatically.' + #13#10 +

            'Mapivo needs it to show its interface.' + #13#10#13#10 +

            'Continue anyway and install it later from the Microsoft website?',

            mbError, MB_YESNO) = IDNO then

      Result := 'Setup cancelled: WebView2 Runtime is required.';

  end;

end;



procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);

begin

  if CurUninstallStep = usPostUninstall then

    MsgBox(ExpandConstant('{cm:UninstallDoneText}'),

           mbInformation, MB_OK);

end;

