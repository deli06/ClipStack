; ClipStack kurulum betiği (Inno Setup 6)
; Doğrudan değil, depo kökündeki build.ps1 ile derlenir; o betik sürümü ve yolları /D ile verir:
;   ISCC.exe /DAppVersion=0.4.0 /DSourceDir=...\artifacts\publish /DRepoRoot=... ClipStack.iss
;
; Kurulum kullanıcı başınadır (yönetici izni istemez): %LOCALAPPDATA%\Programs\ClipStack

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef RepoRoot
  #define RepoRoot ".."
#endif
#ifndef SourceDir
  #define SourceDir RepoRoot + "\artifacts\publish"
#endif

#define AppName "ClipStack"
#define AppExe "ClipStack.exe"
; Uygulamanın tek örnek kontrolünde kullandığı mutex (App.xaml.cs, MutexName). Kurulum/kaldırma sırasında
; ClipStack çalışıyorsa kullanıcıdan kapatması istenir.
#define AppMutexName "ClipStack.SingleInstance"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define ApprovedKey "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"

[Setup]
; AppId kurulumun kimliğidir; sürümler arasında DEĞİŞTİRİLMEMELİ (yoksa güncelleme yerine ikinci kurulum olur).
AppId={{7C1D7E2B-5E4A-4B8E-9C3F-2A6D1B0E9F41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ClipStack katkıcıları
AppCopyright=Copyright © 2026 ClipStack katkıcıları · GPL-3.0-or-later
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} kurulumu

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; .NET 8 için en düşük Windows 10 1607.
MinVersion=10.0.14393

LicenseFile={#RepoRoot}\LICENSE
SetupIconFile={#RepoRoot}\ClipStack\Assets\ClipStack.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern

AppMutex={#AppMutexName}
CloseApplications=yes
RestartApplications=no

OutputBaseFilename=ClipStack-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes

; İmzalama (isteğe bağlı): build.ps1 -Sign ile "Signtool" adlı bir imza aracı tanımlanırsa etkinleşir.
#ifdef SignTool
SignTool={#SignTool}
SignedUninstaller=yes
#endif

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
tr.AutoStart=Windows açılınca ClipStack'i başlat
en.AutoStart=Start ClipStack when Windows starts
tr.LaunchNow=ClipStack'i şimdi başlat
en.LaunchNow=Launch ClipStack now
tr.DeleteData=Pano geçmişiniz, ayarlarınız ve şifreleme anahtarınız da silinsin mi?%n%n%1%n%nBu işlem geri alınamaz. ClipStack'i yeniden kuracaksanız "Hayır" deyin; geçmişiniz korunur.
en.DeleteData=Also delete your clipboard history, settings and encryption key?%n%n%1%n%nThis cannot be undone. Choose "No" if you plan to reinstall ClipStack; your history will be kept.

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\dotnet-THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Uygulamanın kendi "Windows ile başlat" ayarıyla birebir aynı kayıt (StartupManager.cs): aynı ad, aynı argüman.
; Böylece Seçenekler'deki onay kutusu bu kaydı doğru gösterir ve değiştirebilir.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"" --startup"; Tasks: autostart
; Görev Yöneticisi'nden daha önce "devre dışı" bırakıldıysa o işareti temizle; yoksa kayıt olsa da başlamaz.
Root: HKCU; Subkey: "{#ApprovedKey}"; ValueType: none; ValueName: "{#AppName}"; Flags: deletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = '{#RunKey}';
  ApprovedKey = '{#ApprovedKey}';

{ Başlangıç kaydı bu kuruluma mı ait? (Geliştirici başka klasördeki bir kopyayı kaydetmiş olabilir; ona dokunma.) }
function RunEntryPointsToThisInstall(): Boolean;
var
  Command: String;
begin
  Result := RegQueryStringValue(HKCU, RunKey, '{#AppName}', Command) and
            (Pos(Lowercase(ExpandConstant('{app}\{#AppExe}')), Lowercase(Command)) > 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Uygulama "Windows ile başlat"ı kendisi de açabildiği için kaydı her durumda kontrol et. }
    if RunEntryPointsToThisInstall() then
    begin
      RegDeleteValue(HKCU, RunKey, '{#AppName}');
      RegDeleteValue(HKCU, ApprovedKey, '{#AppName}');
    end;
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    { Veriler (şifreli geçmiş, db.key, ayarlar, log, yedekler). Sessiz kaldırmada asla silinmez; varsayılan "Hayır". }
    DataDir := ExpandConstant('{localappdata}\ClipStack');
    if (not UninstallSilent()) and DirExists(DataDir) then
      if MsgBox(FmtMessage(CustomMessage('DeleteData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
