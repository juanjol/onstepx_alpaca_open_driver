; OnStepX ASCOM installer (Inno Setup 6, confirmed 6.7.1 on the windows-latest
; GitHub Actions image at the time this was written).
;
; Packages the two executables the "windows" CI job publishes right before
; calling iscc: OnStepX.AlpacaServer (win-x64, self-contained) at
; artifacts\win-x64, and OnStepX.ComShim (win-x86, framework-dependent net48)
; at artifacts\win-x86. Both paths are relative to the repository root, and
; this script sits one level down in installer\, hence the "..\artifacts\..."
; sources below.

#define AppVersion "0.2.0"
#define AppPublisher "Juanjo Lopez"
#define AppCompany "OnStepX ASCOM"
#define AppNameString "OnStepX ASCOM"

[Setup]
; Fixed for good, like the CLSIDs and AppID in DriverRegistration.cs: this is
; what Windows uses to recognise "the same product" across versions for
; upgrades and Add/Remove Programs. Changing it would make every future
; version look like an unrelated, separately installed product.
AppId={{15FFF8CE-C555-4AE1-AFF0-779F4E5654E8}
AppName={#AppNameString}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (C) 2026 {#AppPublisher}
AppUpdatesURL=https://github.com/juanjolopez/onstepx-ascom
; Distinct from AppPublisher (a person, matching Authors in Directory.Build.props):
; this is the product's Company value from the same file, used for the compiled
; installer .exe's own version resource rather than for Setup's wizard pages.
VersionInfoCompany={#AppCompany}
DefaultDirName={autopf}\{#AppNameString}
DefaultGroupName={#AppNameString}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=OnStepX.Ascom.Setup-{#AppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
; Both payloads are Windows binaries built by a Windows runner (the x86
; ComShim exe runs fine on 64 bit Windows regardless of this setting, this
; only concerns the OS the installer itself accepts and where {autopf}/{sys}
; point). x64compatible is the modern identifier, safe to use since the CI
; image ships Inno Setup 6.7.1, well past the 6.3 release that introduced it.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Needed for all of: writing under Program Files, the HKCR entries
; DriverRegistration.Register writes, and sc.exe service registration. The
; installer runs elevated once for everything instead of ComShim.exe having
; to self-elevate a second time when -regserver runs from [Run].
PrivilegesRequired=admin
UninstallDisplayIcon={app}\OnStepX.AlpacaServer.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Radio-button style exclusive group: exactly one of the three is always
; selected (GroupDescription ties them together, Flags: exclusive makes
; picking one uncheck the others). Service is the default because this
; installer is meant first and foremost for an unattended astronomy rig: the
; Alpaca server has to come back up after a reboot with nobody around to log
; in and click a tray icon. Tray and "neither" cover the interactive setups
; (a desktop shared with other uses, or someone who prefers to start it by
; hand) without making them the default anyone has to opt out of.
Name: "servicetask"; Description: "Run as a Windows service, started automatically at boot (recommended for an unattended mount)"; GroupDescription: "How should OnStepX ASCOM start?"; Flags: exclusive
Name: "traytask"; Description: "Run at login with a system tray icon"; GroupDescription: "How should OnStepX ASCOM start?"; Flags: exclusive unchecked
Name: "nonetask"; Description: "Do not start automatically, launch manually from the Start Menu"; GroupDescription: "How should OnStepX ASCOM start?"; Flags: exclusive unchecked

[Files]
; Kept in separate directories on purpose. The two publishes carry same named
; dependency assemblies (System.Text.Json, System.Memory, System.Buffers and
; the rest of the chain ASCOM.Alpaca.Components pulls in) that are not binary
; compatible between the .NET 8 build and the net48 one. Flattening both into
; {app} would let whichever copy [Files] writes last silently overwrite the
; other, and the loser would fail to load at run time with no obvious cause.
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\win-x86\*"; DestDir: "{app}\ComShim"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppNameString}"; Filename: "{app}\OnStepX.AlpacaServer.exe"; Parameters: "--console"; WorkingDir: "{app}"; Comment: "Run OnStepX ASCOM in the foreground"
Name: "{group}\Uninstall {#AppNameString}"; Filename: "{uninstallexe}"
; Only created when the tray task is picked. {autostartup} resolves to the
; all users Startup folder under an admin install (this one always is, see
; PrivilegesRequired above), so the tray icon comes up for whoever logs in,
; not only the account that ran the installer. Inno removes this shortcut by
; itself on uninstall along with every other [Icons] entry, no extra work
; needed on that side.
Name: "{autostartup}\{#AppNameString}"; Filename: "{app}\OnStepX.AlpacaServer.exe"; Parameters: "--tray"; WorkingDir: "{app}"; Tasks: traytask; Comment: "Start OnStepX ASCOM in the system tray"

[Run]
; COM registration always happens, unconditionally, regardless of which
; startup task above was picked: COM clients (PHD2, SGP, CdC and the rest)
; are independent of how the Alpaca server itself is hosted, so there is no
; task that should skip this.
Filename: "{app}\ComShim\OnStepX.ComShim.exe"; Parameters: "-regserver"; Flags: runhidden waituntilterminated; StatusMsg: "Registering the COM local server..."

; AddWindowsService (see Program.cs) only makes the running process behave
; like a service once the Service Control Manager starts it responding to
; start/stop and using the right working directory. It does not register the
; service with SCM in the first place, which is what sc.exe create does here.
; Quoting note: the whole "binPath=" value has to be one argument containing
; the exe path already quoted (it lives under "Program Files", which has a
; space), so the inner quotes are backslash escaped exactly as sc.exe itself
; requires on its own command line, and doubled again here because Inno's
; script syntax escapes a literal quote inside a string with two quotes.
; Decoded, this sends sc.exe the same command line as:
;   create "OnStepX ASCOM" binPath= "\"C:\Program Files\OnStepX ASCOM\OnStepX.AlpacaServer.exe\" --service" start= auto
Filename: "{sys}\sc.exe"; Parameters: "create ""OnStepX ASCOM"" binPath= ""\""{app}\OnStepX.AlpacaServer.exe\"" --service"" start= auto"; Flags: runhidden waituntilterminated; StatusMsg: "Registering the Windows service..."; Tasks: servicetask
Filename: "{sys}\sc.exe"; Parameters: "start ""OnStepX ASCOM"""; Flags: runhidden waituntilterminated; StatusMsg: "Starting the Windows service..."; Tasks: servicetask

; The tray task gets its Startup shortcut from [Icons] above, which only
; takes effect at the next login. Offering to launch it immediately, gated
; behind the standard "postinstall" checkbox on the wizard's last page, means
; the icon does not wait for a reboot to appear. "nowait" so the wizard does
; not sit there waiting on a process that is meant to keep running.
Filename: "{app}\OnStepX.AlpacaServer.exe"; Parameters: "--tray"; Description: "Launch OnStepX ASCOM now"; Flags: nowait postinstall skipifsilent; Tasks: traytask

[UninstallRun]
; Every one of these runs unconditionally, not gated by which startup task
; was originally selected, and failures are ignored (sc.exe stopping or
; deleting a service that was never created just returns a harmless error
; code, and Inno does not inspect [UninstallRun] exit codes). This is
; deliberately simpler and more robust than trying to key the cleanup off the
; task that was picked at install time: it also correctly tidies up a machine
; where the install was later switched from "service" to "tray" or "none"
; through a repair/reinstall, which a task-gated uninstall entry would miss.
; RunOnceId is required by Inno for every [UninstallRun] entry; the strings
; only need to be unique within this script.
Filename: "{sys}\sc.exe"; Parameters: "stop ""OnStepX ASCOM"""; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete ""OnStepX ASCOM"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
; [UninstallRun] entries execute at the first stage of uninstallation, before
; any files are removed, so OnStepX.ComShim.exe is still on disk here.
Filename: "{app}\ComShim\OnStepX.ComShim.exe"; Parameters: "-unregserver"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterComShim"

[Code]
// Stops the service before [Files] tries to overwrite the executables. This
// only matters on a reinstall or upgrade: a fresh install has no service yet
// and Exec below just fails harmlessly. Without this, upgrading over a
// running "OnStepX ASCOM" service would fail to replace
// OnStepX.AlpacaServer.exe, since SCM keeps it open, and the wizard would
// report a file copy error with no obvious cause.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop "OnStepX ASCOM"', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);

  // "sc stop" only asks SCM to stop the service, it does not wait for the
  // process to actually exit, and the exe stays locked against [Files] until
  // it does. "sc query" is not a way to detect that here: its exit code
  // reflects whether the service is registered at all, not which state it is
  // in, and stopping does not unregister it, so polling it would just spin
  // for no signal. ResultCode is 0 only when a running service actually
  // accepted the stop request; a fresh install, with nothing registered yet,
  // skips the wait entirely. The generic host closes its own transport
  // during ApplicationStopping (see ServerRuntime.ShutdownAsync in
  // Program.cs), which is fast, so this margin is meant to be generous
  // rather than tightly measured: getting it wrong just fails the file copy
  // a moment later with a message that points straight at the real cause.
  if ResultCode = 0 then
    Sleep(3000);

  Result := '';
end;
