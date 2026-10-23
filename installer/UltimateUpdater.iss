; Ultimate Updater installer (Inno Setup).
; Built by build.bat, which passes /DAppVersion. The version lives in the csproj only.
;
; There are two ways to end up with the app, chosen on the mode page right after Welcome: a standard
; installation (per-machine or per-user, Start menu entry, uninstaller, updates in place) or a
; portable copy - one self-contained file dropped into a folder the user picks, with nothing else
; written to this PC. Everything downstream reads IsPortableMode in [Code].

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

#define AppName "Ultimate Updater"
#define AppPublisher "BGCoding"
#define AppExeName "UltimateUpdater.exe"
; Same fixed name build.bat gives the standalone portable download, so both routes to a portable
; copy end up with an identically-named, recognisable file.
#define PortableExeName "Ultimate Updater.exe"
#define AppUrl "https://github.com/wbgcoding/UltimateUpdater"

; An installed copy takes the non-extracting publish (the exe plus WPF's native DLLs beside it); portable
; mode keeps the self-extracting single file above, so it stays one file.
#define InstallStageX64 AddBackslash(SourcePath) + "..\build\publish\installer-win-x64\"
#define InstallStageArm64 AddBackslash(SourcePath) + "..\build\publish\installer-win-arm64\"

; Size of what actually gets installed, read from the real payload at compile time so the figure can
; never drift from what ships: the exe plus every DLL of the installer stage. Exactly one architecture is
; installed and they differ by a few MB, so the larger one is the honest estimate (portable mode then
; over-reports slightly). See ExtraDiskSpaceRequired.
#sub AddStageFile
  #expr StageTotal = StageTotal + FileSize(StageCurrentDir + FindGetFileName(StageFindHandle))
#endsub
#define StageTotal 0
#define StageFindHandle 0
#define StageFindResult 0
#sub SumStageDir
  #expr StageTotal = FileSize(StageCurrentDir + "UltimateUpdater.exe")
  #expr StageFindHandle = FindFirst(StageCurrentDir + "*.dll", 0)
  #for {StageFindResult = StageFindHandle; StageFindResult; StageFindResult = FindNext(StageFindHandle)} AddStageFile
  #if StageFindHandle
    #expr FindClose(StageFindHandle)
  #endif
#endsub
#define StageCurrentDir InstallStageX64
#call SumStageDir
#define PayloadSize StageTotal
#define StageCurrentDir InstallStageArm64
#call SumStageDir
#if StageTotal > PayloadSize
  #define PayloadSize StageTotal
#endif
#if PayloadSize < 0
  #define PayloadSize 0
#endif

[Setup]
; AppId stays exactly as it was under the old name, on purpose: Inno keys an existing installation
; to this GUID, not to AppName, so an existing install upgrades in place instead of leaving a
; duplicate Programs-and-Features entry behind. Never regenerate this for a rename alone.
AppId={{8C4F2A91-3B7D-4E5A-9C21-6D8E4F1B2A73}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; Always show the folder page. The default (auto) SKIPS it once a previous install is detected and
; reuses that folder - which is exactly why picking a folder for a portable copy appeared to do nothing
; after the app had been installed once. Portable must always be able to choose where its file goes.
DisableDirPage=no

; Reuse the folder of an existing installation when Setup runs with no wizard at all - the app's own
; self-updater runs it silently, so nothing else can supply the folder there.
UsePreviousAppDir=yes

; Updating an existing installation is the normal case, and the folder page is prefilled with the
; folder that installation really lives in (see CurPageChanged), so the "directory already exists"
; question has nothing left to warn about. Portable would hit it on every single run too: its default
; is the folder Setup was started from, which by definition exists. And no question asked here can be
; answered on the silent self-update path.
DirExistsWarning=no

; A portable copy leaves nothing behind: no uninstaller files beside the exe, no Programs and Features
; entry. Both directives take a scripted boolean expression, evaluated when the install actually runs,
; so they follow the choice made on the mode page.
Uninstallable=not IsPortableMode
CreateUninstallRegKey=not IsPortableMode

; The user picks per-machine or per-user; nothing is elevated unless per-machine is chosen.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=..\dist
; No space in the artifact name: it ends up in release URLs and on command lines.
OutputBaseFilename=Setup-UltimateUpdater-{#AppVersion}
SetupIconFile=..\src\UltimateUpdater\Assets\app.ico
UninstallDisplayIcon={code:GetAppExePath}
UninstallDisplayName={#AppName}

; The payload entries below carry a Check parameter, because which architecture installs is only known
; at run time - and Setup leaves entries it cannot decide on out of its "space required" figure. That
; left the wizard claiming ~4 MB, the size of the uninstaller alone, for an ~84 MB install. Declaring
; the payload here puts the real number back, and pins the size shown in Programs and Features to it
; instead of letting that be estimated the same way.
#if PayloadSize > 0
ExtraDiskSpaceRequired={#PayloadSize}
UninstallDisplaySize={#PayloadSize}
#endif

; Highest preset Inno offers, and solid so the two architectures' shared bytes are stored once. The
; payload exes are already single-file compressed, so the further gain here is small - but this squeezes
; out what remains without dropping either architecture or the self-contained runtime (compression only, no features cut).
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
SolidCompression=yes
WizardStyle=modern

; Refuse to install on anything older than Windows 10 1809.
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64

; Updating over a running copy is the normal case - the self-updater does exactly that. Setup closes
; the running app through the Restart Manager so the file replacement works without a reboot.
CloseApplications=yes
CloseApplicationsFilter=*.exe
; Left "yes" here, the Restart Manager restarts whatever it
; closed as soon as the files are copied, which is BEFORE the Finish page - the app came back while
; the wizard was still open, and the "start now" checkbox on Finish then had nothing left to do. The
; [Run] section already knew this about the SAME mechanism ("does not do it reliably") for the
; self-updater's own relaunch two lines above its entry. The app now comes back only through the
; Finish page's checkbox or through the self-updater's explicit /RELAUNCH=1, never through this.
; Do not set this back to "yes".
RestartApplications=no

; LicenseFile carries the MIT text followed by the disclaimer, both in one page the user has to
; accept before continuing. InfoBeforeFile was dropped here: it used to show the disclaimer alone
; on its own page, which would now just repeat the second half of the licence page word for word.
[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"; LicenseFile: "license.en.txt"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"; LicenseFile: "license.de.txt"

[CustomMessages]
en.AutostartTask=Start {#AppName} automatically when I sign in to Windows
en.LaunchAfter=Launch {#AppName} now
en.ModeCaption=Installation type
en.ModeDescription=Choose how you would like to set up {#AppName}, then click Next.
en.ModeInstallTitle=Standard installation (recommended)
en.ModeInstallInfo=Creates a Start menu entry and an optional autostart, and keeps itself up to date in place.
en.ModePortableTitle=Portable
en.ModePortableInfo=Places a single, self-contained program file in a folder of your choice. Nothing else is added to this PC.
en.PortableDirDescription=Choose a folder for the portable copy of {#AppName}. Setup suggests a folder of its own next to this installer and places {#PortableExeName} into it. Nothing else is written to this PC.
en.UpdateDirDescription={#AppName} is already installed on this PC and will be updated in the folder shown below. Click Next to continue, or Browse to choose a different folder.
de.AutostartTask={#AppName} automatisch bei der Windows-Anmeldung starten
de.LaunchAfter={#AppName} jetzt starten
de.ModeCaption=Art der Installation
de.ModeDescription=Wählen Sie, wie {#AppName} eingerichtet werden soll, und klicken Sie auf Weiter.
de.ModeInstallTitle=Standardinstallation (empfohlen)
de.ModeInstallInfo=Legt einen Startmenü-Eintrag und einen optionalen Autostart an und aktualisiert sich selbst an Ort und Stelle.
de.ModePortableTitle=Portabel
de.ModePortableInfo=Legt eine einzelne, eigenständige Programmdatei in einem Ordner Ihrer Wahl ab. Sonst wird nichts auf diesem PC hinzugefügt.
de.PortableDirDescription=Wählen Sie einen Ordner für die portable Version von {#AppName}. Setup schlägt einen eigenen Ordner neben diesem Installationsprogramm vor und legt {#PortableExeName} darin ab. Sonst wird nichts auf diesem PC gespeichert.
de.UpdateDirDescription={#AppName} ist auf diesem PC bereits installiert und wird im unten angezeigten Ordner aktualisiert. Klicken Sie auf Weiter, oder auf Durchsuchen, um einen anderen Ordner zu wählen.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutostartTask}"

[Files]
; The published single-file exe for the architecture being installed, taken from the build stage.
; dist\ deliberately holds only the x64 portable exe and this Setup, so the arm64 build is sourced
; from build\publish\ instead of from dist\ (build.bat). The name it lands under depends on the mode
; chosen on the early wizard page (see GetExeName in [Code]): the installed copy keeps the plain exe
; name, the portable copy gets the same fixed, recognisable name the standalone download uses. In
; portable mode this one file is the entire installation - it goes straight into the chosen folder,
; with no subfolder and no uninstaller (Uninstallable above).
;
; Install mode takes the non-extracting publish: the exe plus WPF's native DLLs beside it, so nothing is
; unpacked into a folder the unelevated user owns. Portable mode keeps the self-extracting single file,
; because it must stay one file and a portable copy self-updates by swapping that file alone. The
; installer stage also holds the .pdb (absolute source paths), so only the exe and the DLLs are listed.
Source: "..\build\publish\win-x64\UltimateUpdater.exe"; DestDir: "{app}"; DestName: "{code:GetExeName}"; \
    Check: IsPortableMode and (not IsArm64); Flags: ignoreversion
Source: "..\build\publish\win-arm64\UltimateUpdater.exe"; DestDir: "{app}"; DestName: "{code:GetExeName}"; \
    Check: IsPortableMode and IsArm64; Flags: ignoreversion
Source: "..\build\publish\installer-win-x64\UltimateUpdater.exe"; DestDir: "{app}"; DestName: "{code:GetExeName}"; \
    Check: (not IsPortableMode) and (not IsArm64); Flags: ignoreversion
Source: "..\build\publish\installer-win-x64\*.dll"; DestDir: "{app}"; \
    Check: (not IsPortableMode) and (not IsArm64); Flags: ignoreversion
Source: "..\build\publish\installer-win-arm64\UltimateUpdater.exe"; DestDir: "{app}"; DestName: "{code:GetExeName}"; \
    Check: (not IsPortableMode) and IsArm64; Flags: ignoreversion
Source: "..\build\publish\installer-win-arm64\*.dll"; DestDir: "{app}"; \
    Check: (not IsPortableMode) and IsArm64; Flags: ignoreversion

[Icons]
; Portable mode skips the whole Tasks page (ShouldSkipPage), so neither task is ever selected there -
; the explicit Check here is belt-and-braces, and the Start Menu entry additionally needs its own guard
; since it carries no Task of its own.
Name: "{group}\{#AppName}"; Filename: "{code:GetAppExePath}"; Check: not IsPortableMode
Name: "{autodesktop}\{#AppName}"; Filename: "{code:GetAppExePath}"; Tasks: desktopicon; Check: not IsPortableMode

[Run]
; Autostart is written by the APP, not from here. Setup's own HKCU is the hive of whatever account
; approved the elevation prompt, which on a machine with a separate administrator account is not the
; person installing - their Run key would stay empty while a stranger's filled up. runasoriginaluser
; puts the app back in the installing user's context, where its own HKCU is the right one. It writes
; the value and exits; nothing is shown. Never in portable mode: a portable copy leaves nothing behind.
; Not on a self-update (/RELAUNCH=1): Inno reuses the previous install's task choices in a silent run, so
; every update switched autostart back on for a user who had turned it off inside the app.
Filename: "{code:GetAppExePath}"; Parameters: "--set-autostart"; \
    Flags: runhidden waituntilterminated runasoriginaluser; \
    Tasks: autostart; Check: (not IsPortableMode) and (not WantsRelaunch)
; Unticking the task in an interactive (re)install means "do not start with Windows": --clear-autostart
; was implemented in the app but never sent, so an existing value simply stayed.
Filename: "{code:GetAppExePath}"; Parameters: "--clear-autostart"; \
    Flags: runhidden waituntilterminated runasoriginaluser; \
    Tasks: not autostart; Check: (not IsPortableMode) and (not WizardSilent)
; runasoriginaluser: without it a per-machine install would start the app with Setup's admin token,
; and the app is required to run unelevated.
Filename: "{code:GetAppExePath}"; Description: "{cm:LaunchAfter}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser
; The self-updater replaced a copy that was running and has to put it back. The Restart Manager
; offers to do that and does not do it reliably, so the app asks for it explicitly with /RELAUNCH=1.
; For an all-users install the app starts this Setup already elevated (Verb=runas), so Setup has no
; unelevated "original user" and runasoriginaluser would bring the app back as administrator - possibly
; as a different admin account with its own %APPDATA%. Starting it through explorer.exe hands the launch to
; the signed-in user's own, unelevated shell instead.
Filename: "{code:GetAppExePath}"; Flags: nowait runasoriginaluser; Check: WantsRelaunch and (not IsAdminInstallMode)
Filename: "{win}\explorer.exe"; Parameters: """{code:GetAppExePath}"""; Flags: nowait; \
    Check: WantsRelaunch and IsAdminInstallMode

[Registry]
; Creates nothing - the app writes the value itself (see [Run]). This entry exists only so uninstalling
; takes it away again. [UninstallRun] cannot do the same job: it has no runasoriginaluser flag, so it
; would look at the elevating account's hive rather than the user's. Uninstalling from the same account
; that installed - the ordinary case - therefore removes the value cleanly; an uninstall approved with a
; different administrator account may leave it, and the app's own autostart switch clears it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "UltimateUpdater"; Flags: uninsdeletevalue; Check: not IsPortableMode

[UninstallDelete]
; The marker file, and nothing else. A standard installation keeps its data in %APPDATA% (AppPaths
; decides that from the marker), so it never creates {app}\data at all - while an installation into a
; folder that already existed (D:\Tools, say) may well find someone else's "data" folder there.
; Removing that on uninstall would be silent data loss for a folder this installer never wrote.
Type: files; Name: "{app}\.installed"

[Code]
const
  { Where Inno records a standard installation of this AppId - it appends "_is1" to the AppId. Read
    to find out whether the app is already installed and, if so, where. Kept as a plain literal:
    nothing expands constants in it, so the braces are just characters. }
  UninstallKeyName =
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8C4F2A91-3B7D-4E5A-9C21-6D8E4F1B2A73}_is1';
  AppPathValueName = 'Inno Setup: App Path';

var
  { The mode page, shown right after Welcome and before the directory page: everything downstream
    (default folder, whether Tasks/Icons/registry/marker happen, the installed file name, whether an
    uninstaller is built at all) reads the radio buttons rather than a Task, because a Task is only
    chosen AFTER the directory page in Inno's fixed page order - far too late to steer it. }
  ModePage: TWizardPage;
  InstallRadio, PortableRadio: TNewRadioButton;
  { Kept so the block can be re-centred once the page is actually shown: InitializeWizard runs before the
    wizard form has its final size, so SurfaceHeight is not yet the real one there. }
  ModeInstallInfo, ModePortableInfo: TNewStaticText;
  { Which mode the directory page was last prepared for: -1 = not yet, 0 = install, 1 = portable.
    Keeps Back/Next from overwriting a folder the user picked. }
  DirPageMode: Integer;
  { True while the wizard is being held above other windows so it cannot start hidden behind them. }
  HoldingFront: Boolean;
  { True once the foreground has been asked for, so the first page that appears does it and no
    later page repeats it. }
  FrontRequested: Boolean;

{ Windows refuses SetForegroundWindow to a process that is not already in front, which is exactly the
  position Setup is in when it is launched from a browser, Explorer or another app - so the call alone is
  not enough and the window has to be held on top until it really has the focus.

  Measured live: fsStayOnTop + BringToFront + a bare SetForegroundWindow call, launched from an
  already-focused console window, exactly the scenario this comment describes, did NOT bring Setup's wizard to the front; the console stayed
  on top the entire run, confirmed by screenshot. Topmost alone is not enough either: a window can be
  WS_EX_TOPMOST and still lose the ACTIVE/focused slot to whatever else is already marked topmost or
  was more recently activated, which is what a plain-launched admin console can already be. }
function SetForegroundWindow(Wnd: HWND): BOOL;
  external 'SetForegroundWindow@user32.dll stdcall';

function GetForegroundWindow: HWND;
  external 'GetForegroundWindow@user32.dll stdcall';

function GetWindowThreadProcessId(Wnd: HWND; ProcessId: Longint): DWORD;
  external 'GetWindowThreadProcessId@user32.dll stdcall';

function GetCurrentThreadId: DWORD;
  external 'GetCurrentThreadId@kernel32.dll stdcall';

function AttachThreadInput(AttachTo, AttachFrom: DWORD; Attach: BOOL): BOOL;
  external 'AttachThreadInput@user32.dll stdcall';

function SwitchToThisWindow(Wnd: HWND; AltTab: BOOL): Longint;
  external 'SwitchToThisWindow@user32.dll stdcall';

function SetWindowPos(Wnd: HWND; WndInsertAfter: Longint; X, Y, Cx, Cy: Integer; Flags: UINT): BOOL;
  external 'SetWindowPos@user32.dll stdcall';

const
  HWND_TOPMOST = -1;
  HWND_NOTOPMOST = -2;
  SWP_NOMOVE = $2;
  SWP_NOSIZE = $1;

{ The actual fix: SetForegroundWindow's own refusal is keyed to which THREAD last received user
  input, not which process. Temporarily attaching Setup's UI thread to the current foreground
  window's input state removes that restriction for the duration of the attachment - the standard
  Win32 workaround for exactly this refusal, stronger than relying on WS_EX_TOPMOST alone. Detaching
  again immediately after is required: leaving two threads attached would make them share input
  state (focus, key state, caret) for as long as both live, which is not something Setup should be
  doing to some other, unrelated foreground process for its whole run.

  Measured on a test VM: the AttachThreadInput trick above still lost
  on the very FIRST page (ModePage, no elevation involved yet) - GetForegroundWindow still named
  something else right after SetForegroundWindow returned. SwitchToThisWindow does not go through
  the same foreground-lock check user32 applies to SetForegroundWindow, so it is tried as a second
  attempt only when the first one measurably failed; the TOPMOST/NOTOPMOST toggle then forces the
  window above whatever still holds the slot without leaving it pinned on top for the rest of the
  run (a plain TOPMOST flag alone is not enough by itself). }
procedure ForceForeground(Wnd: HWND);
var
  ForegroundWnd: HWND;
  ForegroundThread, OwnThread: DWORD;
  Attached: BOOL;
begin
  ForegroundWnd := GetForegroundWindow;
  OwnThread := GetCurrentThreadId;
  Attached := False;

  if (ForegroundWnd <> 0) and (ForegroundWnd <> Wnd) then
  begin
    ForegroundThread := GetWindowThreadProcessId(ForegroundWnd, 0);
    if (ForegroundThread <> 0) and (ForegroundThread <> OwnThread) then
      Attached := AttachThreadInput(OwnThread, ForegroundThread, True);
  end;

  SetForegroundWindow(Wnd);

  if Attached then
    AttachThreadInput(OwnThread, ForegroundThread, False);

  if GetForegroundWindow <> Wnd then
  begin
    SwitchToThisWindow(Wnd, True);
    SetWindowPos(Wnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE);
    SetWindowPos(Wnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE);
  end;
end;

function IsArm64: Boolean;
begin
  Result := ProcessorArchitecture = paArm64;
end;

{ True once the user has picked Portable on the mode page. False before the page has been seen at
  all, which is what a silent run needs: the self-updater shows no wizard, so nothing is selected
  there and it always behaves like a standard installation. }
function IsPortableMode: Boolean;
begin
  Result := (PortableRadio <> nil) and PortableRadio.Checked;
end;

{ The file name for whichever mode is currently chosen - one function so [Files], [Icons], [Run] and
  UninstallDisplayIcon all agree on what was actually written to the install folder. }
function GetExeName(Param: String): String;
begin
  if IsPortableMode then
    Result := '{#PortableExeName}'
  else
    Result := '{#AppExeName}';
end;

function GetAppExePath(Param: String): String;
begin
  Result := ExpandConstant('{app}\') + GetExeName('');
end;

{ The folder an existing standard installation lives in, or an empty string if there is none.

  Only the hive for the scope this run actually installs into is consulted. Reading HKLM first
  regardless of scope was wrong in both directions: a per-user run found the machine-wide
  installation and prefilled a Program Files folder it cannot write to, and with both scopes present
  it would have updated the other installation instead of its own. }
function ExistingInstallDir: String;
var
  Hive: Integer;
begin
  Result := '';
  if IsAdminInstallMode then
    Hive := HKLM
  else
    Hive := HKCU;

  if not RegQueryStringValue(Hive, UninstallKeyName, AppPathValueName, Result) then
    Result := '';
  if (Result <> '') and not DirExists(Result) then
    Result := '';
end;

function AddModeRadio(const Title: String; const ALeft, AWidth: Integer): TNewRadioButton;
begin
  Result := TNewRadioButton.Create(ModePage);
  Result.Parent := ModePage.Surface;
  Result.Caption := Title;
  Result.Font.Style := [fsBold];
  Result.Left := ALeft;
  Result.Width := AWidth;
  Result.Height := ScaleY(19);
end;

function AddModeInfo(const Description: String; const ALeft, AWidth: Integer): TNewStaticText;
begin
  Result := TNewStaticText.Create(ModePage);
  Result.Parent := ModePage.Surface;
  Result.Caption := Description;
  Result.WordWrap := True;
  Result.Left := ALeft;
  Result.Width := AWidth;
  Result.AdjustHeight;
end;

{ A hand-built page instead of CreateInputOptionPage: that one draws every option on a single line,
  which forced the short name and its explanation into one long sentence. Here each radio carries its
  own wrapped description underneath, and the whole block sits centred on the page both ways: a fixed
  content width, horizontally centred in the wizard surface (so the two choices no longer hug the left
  edge), with the descriptions aligned just under their radio's caption. }
{ Vertically centres the two choices in whatever height the page surface actually has. Called when the
  page is shown, not when it is built: at build time the wizard form has not been sized yet, so
  SurfaceHeight is still a placeholder and the block ended up above centre. }
procedure CenterModeBlock;
var
  TitleGap, BlockGap, BlockHeight, Available, Top: Integer;
begin
  if (InstallRadio = nil) or (ModeInstallInfo = nil) then
    Exit;

  TitleGap := ScaleY(4);
  BlockGap := ScaleY(22);

  { Re-measured HERE, not when the controls were built: AdjustHeight only knows a description's real
    wrapped height once the page is laid out in the font it actually renders in, and a description that
    wraps onto a second line was counted as one. That undercount is what left the block off centre however
    the arithmetic was written. }
  ModeInstallInfo.AdjustHeight;
  ModePortableInfo.AdjustHeight;

  BlockHeight := InstallRadio.Height + TitleGap + ModeInstallInfo.Height + BlockGap +
    PortableRadio.Height + TitleGap + ModePortableInfo.Height;

  { The surface is the whole area between the header and the buttons, so centring in it is what reads as
    centred in the window; the notebook height is the fallback if the surface reports nothing usable. }
  Available := ModePage.SurfaceHeight;
  if Available <= BlockHeight then
    Available := WizardForm.InnerNotebook.Height;

  Top := (Available - BlockHeight) div 2;
  if Top < 0 then
    Top := 0;

  InstallRadio.Top := Top;
  ModeInstallInfo.Top := InstallRadio.Top + InstallRadio.Height + TitleGap;
  PortableRadio.Top := ModeInstallInfo.Top + ModeInstallInfo.Height + BlockGap;
  ModePortableInfo.Top := PortableRadio.Top + PortableRadio.Height + TitleGap;
end;

procedure InitializeWizard;
var
  InstallInfo, PortableInfo: TNewStaticText;
  Indent, Surface, ContentWidth, BlockLeft, TitleGap, BlockGap, Top: Integer;
begin
  ModePage := CreateCustomPage(wpWelcome,
    CustomMessage('ModeCaption'), CustomMessage('ModeDescription'));

  Surface := ModePage.SurfaceWidth;

  { Fixed, comfortable reading width, centred in the surface. Never wider than the surface itself. }
  ContentWidth := ScaleX(430);
  if ContentWidth > Surface then
    ContentWidth := Surface;
  BlockLeft := (Surface - ContentWidth) div 2;

  { No indent: the description starts flush under its title rather than stepped in from it. }
  Indent := 0;
  TitleGap := ScaleY(4);
  BlockGap := ScaleY(22);

  InstallRadio := AddModeRadio(CustomMessage('ModeInstallTitle'), BlockLeft, ContentWidth);
  InstallInfo := AddModeInfo(CustomMessage('ModeInstallInfo'), BlockLeft + Indent, ContentWidth - Indent);
  PortableRadio := AddModeRadio(CustomMessage('ModePortableTitle'), BlockLeft, ContentWidth);
  PortableInfo := AddModeInfo(CustomMessage('ModePortableInfo'), BlockLeft + Indent, ContentWidth - Indent);

  ModeInstallInfo := InstallInfo;
  ModePortableInfo := PortableInfo;

  { A first pass so the page is never laid out at all wrong; CurPageChanged centres it again with the
    surface's real height once the page is actually shown. }
  Top := (ModePage.SurfaceHeight - (InstallRadio.Height + TitleGap + InstallInfo.Height + BlockGap +
    PortableRadio.Height + TitleGap + PortableInfo.Height)) div 2;
  if Top < 0 then
    Top := 0;
  InstallRadio.Top := Top;
  InstallInfo.Top := InstallRadio.Top + InstallRadio.Height + TitleGap;
  PortableRadio.Top := InstallInfo.Top + InstallInfo.Height + BlockGap;
  PortableInfo.Top := PortableRadio.Top + PortableRadio.Height + TitleGap;

  InstallRadio.Checked := True;
  DirPageMode := -1;
end;

{ Dropped as soon as the wizard genuinely has the focus, so Setup is not left sitting above everything
  else for the rest of the run. }
procedure WizardActivated(Sender: TObject);
begin
  if HoldingFront then
  begin
    HoldingFront := False;
    WizardForm.FormStyle := fsNormal;
  end;
end;

{ Setup kept starting behind the window that launched it, which looks like nothing happened at all.

  Setting fsStayOnTop and dropping it again in the next statement cannot work:
  the two changes collapse into no change before the window manager ever sees a top-most window, and
  BringToFront on its own is subject to Windows' foreground lock, which denies the foreground to a process
  that is not already in it. So the wizard is held on top and only released once it has actually been
  activated. }
procedure BringWizardToFront;
begin
  if FrontRequested then
    Exit;

  FrontRequested := True;
  HoldingFront := True;
  { The handler goes on BEFORE the activation attempt: if the window comes to the front immediately,
    OnActivate fires during ForceForeground, and hooking it afterwards would miss that and leave
    Setup top-most for the rest of the run. }
  WizardForm.OnActivate := @WizardActivated;
  WizardForm.FormStyle := fsStayOnTop;
  WizardForm.BringToFront;
  ForceForeground(WizardForm.Handle);
end;

{ Portable mode skips the whole Tasks page - neither task (desktop icon, autostart) means anything for
  a copy that is meant to leave nothing behind on the machine. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpSelectTasks) and IsPortableMode;
end;

{ The directory page is shared by both modes (there is still always a folder to put the exe in), but
  what it says and suggests differs. Portable suggests the folder Setup itself was started from, so
  the file lands next to the download or on the stick it was run from; a standard installation
  suggests the folder an existing installation already uses, and Program Files only when there is
  none. Rewritten only when the mode changed, so going Back and Next again never throws away a folder
  the user browsed to or typed. }
procedure CurPageChanged(CurPageID: Integer);
var
  Mode: Integer;
  Existing: String;
begin
  { The first page that actually appears is where Setup can first be seen - and where it was being
    left behind whatever window started it, so it asks for the foreground here.

    NOT keyed to wpWelcome, because Inno's default
    is DisableWelcomePage=yes, this script does not turn it back on, so wpWelcome never becomes the
    current page and the call would sit behind a condition that could not be true.
    BringWizardToFront guards itself, so this fires exactly once. }
  BringWizardToFront;

  { The mode page: centre the two choices now that the surface has its real height, and pull the header's
    description text out to the same left edge as the page title, instead of the indent Inno gives it. }
  if (ModePage <> nil) and (CurPageID = ModePage.ID) then
  begin
    CenterModeBlock;
    WizardForm.PageDescriptionLabel.Left := WizardForm.PageNameLabel.Left;
    WizardForm.PageDescriptionLabel.Width := WizardForm.PageNameLabel.Width;
    Exit;
  end;

  if CurPageID <> wpSelectDir then
    Exit;

  Mode := 0;
  if IsPortableMode then
    Mode := 1;
  if Mode = DirPageMode then
    Exit;
  DirPageMode := Mode;

  if IsPortableMode then
  begin
    WizardForm.SelectDirLabel.Caption := CustomMessage('PortableDirDescription');
    { Next to Setup itself, in a folder of its own rather than loose in the download folder
      so a portable copy never scatters its data file beside unrelated downloads. }
    WizardForm.DirEdit.Text := AddBackslash(ExpandConstant('{src}')) + '{#AppName}';
  end
  else
  begin
    Existing := ExistingInstallDir;
    if Existing <> '' then
    begin
      WizardForm.SelectDirLabel.Caption := CustomMessage('UpdateDirDescription');
      WizardForm.DirEdit.Text := Existing;
    end
    else
    begin
      WizardForm.SelectDirLabel.Caption := SetupMessage(msgSelectDirDesc);
      WizardForm.DirEdit.Text := ExpandConstant('{autopf}\{#AppName}');
    end;
  end;
end;

{ Uninstalling while the app runs blocks on files that cannot be deleted, and a silent uninstall has
  nobody to ask. Measured on a test machine, it waits for ever. So the app is closed first, always:
  an interactive uninstall would have asked for exactly this anyway, and there is nothing unsaved to
  lose. Deliberately NOT conditional on UninstallSilent, which did not report silent runs reliably.

  Closed by PATH, not by image name. taskkill /IM matches every process with that name anywhere on
  the machine, so removing an installed copy also shot down a portable copy running from a stick or
  another folder - possibly in the middle of its own install or cleanup run. Only a copy inside the
  installation folder can hold this uninstall's files, and that is the only one closed. Both names
  are covered because that folder may hold either (portable first, then installed over). }
function PsQuote(const Value: String): String;
begin
  { A single quote inside a PowerShell single-quoted string is written twice. A folder name may
    legitimately contain one, and without this the rest of the command would be read as code. }
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
end;

function InitializeUninstall: Boolean;
var
  ResultCode: Integer;
  Script: String;
begin
  Result := True;
  Script :=
    '$p = @(''' + PsQuote(ExpandConstant('{app}\{#AppExeName}')) + ''', ''' +
    PsQuote(ExpandConstant('{app}\{#PortableExeName}')) + '''); ' +
    'Get-Process -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -and ($p -contains $_.Path) } | ' +
    'Stop-Process -Force -ErrorAction SilentlyContinue';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Set by the self-updater so the app it just closed is started again. }
function WantsRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

{ An installed copy is marked with a file next to the exe, which the app checks at runtime to decide
  between installed and portable data locations. A portable install deliberately omits it. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  MarkerPath: string;
begin
  if (CurStep = ssPostInstall) and not IsPortableMode then
  begin
    MarkerPath := ExpandConstant('{app}\.installed');
    SaveStringToFile(MarkerPath, '', False);
  end;
end;

{ Settings and history in %APPDATA% are the user's and are always left behind, as Windows apps
  normally do. There used to be a Yes/No box asking about it at the very end of the uninstall: it is
  not suppressed by /SUPPRESSMSGBOXES, so an unattended removal stopped there and waited forever with
  no window to answer. Deleting the question removes the failure mode; the README says where the
  folder is for anyone who wants it gone. }
