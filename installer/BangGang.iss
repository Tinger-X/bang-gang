; 帮帮（BangGang）安装脚本。由 tools/build-installer.ps1 调 ISCC.exe 编译。
;
; 几条不显然的取舍：
;
; * 每用户安装（{localappdata}\Programs\BangGang）+ PrivilegesRequired=lowest。
;   不弹 UAC 只是一半理由；另一半是应用把 settings.json / chats / images 写在
;   exe 同目录（AppContext.BaseDirectory），装到 Program Files 那种只读位置会让
;   它一启动就写盘失败 —— 见 AppSettings.Save / ChatStore.NewImagePath。
;
; * 上述运行期数据**不在** [Files] 清单里。Inno 的卸载程序只删自己记录过的文件，
;   所以这些数据默认会被留下；卸载收尾会明确问用户要不要一并清除，见 [Code]。
;
; * **绝对不要加 [InstallDelete] / [UninstallDelete]。** 那是按通配符无差别删的，
;   而用户数据就住在 {app} 里，加一条就会在每次升级时静默清空聊天记录。
;   [Files] 不列举这些文件，就是它们唯一的防线。
;
; * AppId 必须永不改动：升级时靠它认领旧安装，改了就会并排装出第二份。
;
; 本文件必须保存为 UTF-8 **带 BOM**。Inno 6 在缺 BOM 时按 ANSI 解读脚本，
; 所有中文都会变成乱码 —— 且不报错。.editorconfig 里已钉住 charset，
; build-installer.ps1 每次编译前也会校验。

#define MyAppName "帮帮"
#define MyAppExeName "BangGang.exe"
#define MyAppPublisher "Tinger"
#define MyAppId "{{7C4B9E31-5A62-4F18-9D7C-2B8E6A0F4C55}"
; 必须和 Program.cs 里 new Mutex(true, ...) 的名字一字不差。
#define MyAppMutex "Local\BangGang_SingleInstance"

; 版本号由 build-installer.ps1 从 BangGang.csproj 读出来传进来。这里的兜底值是为了
; 直接在编辑器里单跑 ISCC 时也能编译，不代表真实版本。
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

; 同一份脚本出两个安装包，差别只有两处：打哪棵 publish 目录，以及装之前要不要
; 检查本机的 .NET 运行时。
;
; 判定用的是「有没有传 /DSelfContained」这个**定义与否**，而不是它的值。ISPP 的
; #if 拿整数当条件时并不按 0=假 来算 —— 实测 /DSelfContained=0 照样走真分支，
; 两个包会静默编成一模一样（症状是 ISCC 打三条 "Variable never used"，因为 #else
; 那段根本没被包含进去）。#ifdef 没有这层歧义，所以别给它补什么默认值。
;
;   传 /DSelfContained=1  → 自带 .NET 8 运行时，目标机什么都不用装（~49MB）
;   不传                  → 不自带，目标机需预装 .NET 8 Desktop Runtime（~2MB）

; 要打进安装包的 publish 目录，相对本文件所在的 installer\。
;
; 刻意推出来、而不是让构建脚本传路径进来：/D 传进来的值会走 ISPP 的表达式解析，
; 带反斜杠的 Windows 路径得额外加引号才安全；推导则两边只共享一个约定（目录名），
; 改的时候记得 tools\build-installer.ps1 里也有一份同名目录。
#ifndef PublishDir
  #ifdef SelfContained
    #define PublishDir "..\build\publish\selfcontained"
  #else
    #define PublishDir "..\build\publish\frameworkdependent"
  #endif
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; 四段式版本号，写进 Setup.exe 的版本资源，资源管理器属性页里看得到。
VersionInfoVersion={#MyAppVersion}.0
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\BangGang
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; auto：全新安装时让用户能改路径，覆盖升级时直接沿用旧路径不打扰。
DisableDirPage=auto
UsePreviousAppDir=yes
PrivilegesRequired=lowest
; .NET 8 桌面运行时要求 Win10 1809 起。
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 相对本文件（installer\）解析。
OutputDir=..\dist\installer
; 文件名必须区分开：两个包是同一个 AppId、同一个版本号，只有这一处能把它们分开。
#ifdef SelfContained
OutputBaseFilename=BangGang-Setup-{#MyAppVersion}-with-runtime
#else
OutputBaseFilename=BangGang-Setup-{#MyAppVersion}-without-runtime
#endif
SetupIconFile=..\assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 应用是 ShowInTaskbar=false 的无边框窗口，没有托盘也没有任务栏按钮，
; 用户不一定会先退出它。交给 Restart Manager 去识别并关掉占用文件的进程。
;
; 注意：这里**不能**再加 [Setup] 的 AppMutex 指令。AppMutex 是在向导启动前就
; while 循环挡住安装，会在 CloseApplications 生效之前先把用户拦死，
; 等于废掉自动关闭 —— 而让用户自己去关一个没有任务栏按钮的程序是很糟的体验。
; 卸载那头没有 Restart Manager，所以在 [Code] 里单独查互斥体。
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; 整棵 publish 目录树一次打包（哪一棵由构建脚本用 /DPublishDir 决定）。
;   ignoreversion —— 强制按 publish 结果覆盖。默认的「版本相同就跳过」在这里是错的：
;     框架 dll 的版本跨我们两个 release 不变，但换 SDK 时内容会变，跳过就会新旧混装。
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 直接放在「所有程序」根下，不为单 exe 应用再套一层目录。
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

#ifdef SelfContained
#else
; 只有「不自带运行时」这个包需要这一段。
;
; 从「自带运行时」那个包换装到这个包时，上一版那 ~160MB 运行时 dll **不会**被 Inno
; 自动清掉 —— [Files] 只覆盖和新增，从不删除。实测换装后目录仍是 466 个文件 / 165MB，
; 而且因为 coreclr / hostfxr 都还在，应用会继续按自包含的方式跑，跟这个包宣称的行为
; 对不上。所以这里先清一遍（[InstallDelete] 在 [Files] 之前执行）。
;
; **这个通配符是安全的，也是全文唯一允许用通配符的地方**：应用的用户数据是
; settings.json / chats\ / images\ / crash.log，没有一个是 .dll。千万不要把它扩成
; *.json / *.log / 整个目录 —— settings.json 就是 .json，那样会直接删掉用户的聊天记录。
;
; 已知残留：通配符**不递归**，所以各语言子目录里的 *.resources.dll（cs\ de\ zh-Hans\
; runtimes\ 等，约 20MB）会留下来。要清掉它们就得按目录删，而那正是唯一可能误伤
; chats\ / images\ 的操作 —— 权衡下来宁可留 20MB 惰性文件。这一条只有「先装自带运行时
; 的包、再换装这个包」才会遇到；从头装这个包是干净的 5MB。
[InstallDelete]
Type: files; Name: "{app}\*.dll"
#endif

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveUserData: Boolean;

// 注意 #if 这里只能写正条件：ISPP 表达式不认 C 风格的 ! ，也不认 Pascal 的 not
// （两种写法都在这个位置报过 "Error on line … Column …"，且错误信息是空的）。
// 要否定就把分支反过来写，见下面用 #else 分出来的那段。
#ifdef SelfContained
// 自包含的包把 .NET 8 运行时一起打进去了，不必也不能再查本机 —— 查了反而会挡住
// 本来装得上的机器。
#else
// .NET 8 的桌面运行时装在 {pf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.x。
// 这里只认 8.x：默认的 roll-forward 策略是 Minor，装了 .NET 9 也满足不了一个针对
// 8.0 编译的应用，所以不能看见 dotnet 目录就当通过。
function HasDotNet8DesktopRuntime(): Boolean;
var
  FindRec: TFindRec;
  Base: String;
begin
  Result := False;
  Base := ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(Base + '\8.*', FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;
#endif

function InitializeSetup(): Boolean;
var
  Choice, Rc: Integer;
  Msg: String;
begin
  Result := True;

#ifdef SelfContained
  // 自带运行时，没有要先决条件。
#else
  // 静默安装不弹这个：那种场景下没人能回答问题，多半还是脚本在按已知环境部署。
  if (not WizardSilent) and (not HasDotNet8DesktopRuntime()) then
  begin
    Msg := '未检测到 .NET 8 桌面运行时。' + #13#10 + #13#10 +
           '这个安装包不自带运行时，装完之后帮帮无法启动，' + #13#10 +
           '需要另外安装 .NET 8 Desktop Runtime（约 55 MB）。' + #13#10 + #13#10 +
           '是：打开下载页面，稍后再装帮帮' + #13#10 +
           '否：仍然继续安装' + #13#10 +
           '取消：退出安装';
    Choice := MsgBox(Msg, mbConfirmation, MB_YESNOCANCEL);
    if Choice = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0',
                '', '', SW_SHOWNORMAL, ewNoWait, Rc);
      // 让他先把运行时装上，别在这台机器上留一个跑不起来的帮帮。
      Result := False;
    end
    else if Choice = IDCANCEL then
      Result := False;
  end;
#endif
end;

function InitializeUninstall(): Boolean;
var
  AppDir, KillCmd: String;
  Rc: Integer;
begin
  Result := True;
  RemoveUserData := False;
  AppDir := ExpandConstant('{app}');
  KillCmd := '/IM {#MyAppExeName}';

  { 卸载这头没有 Restart Manager（CloseApplications 只管安装），只能自己看单实例互斥体，
     否则应用开着的时候卸载会删不掉被占用的文件 —— 而且卸载器照样返回 0，是静默的半卸载。

     先温和地关一次：taskkill 不带 /F 就是发 WM_CLOSE，会走应用自己的退出流程；
     帮帮没有托盘、ShowInTaskbar=false，让用户自己去找那个自绘的关闭按钮并不容易。

     这一步**静默卸载也得做**：它不需要人应答，没理由被下面的静默分支挡掉。
     （曾经的写法是在这里之前就 `if UninstallSilent then Exit`，结果静默卸载撞上
       正在运行的实例时留下 42 个文件加一个没删掉的 exe，退出码还是 0。） }
  if CheckForMutexes('{#MyAppMutex}') then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), KillCmd, '', SW_HIDE, ewWaitUntilTerminated, Rc);
    Sleep(1500);
  end;

  { 静默卸载（unins000.exe /SILENT，或安装程序在升级时带起来的旧版卸载）背后没有人，
     既不该弹窗，更不能进下面那个重试循环 —— 无人应答时它会一直转。保持默认：留着数据。 }
  if UninstallSilent then
    Exit;

  { 还没退就交给用户定夺，不能装作没看见往下走。 }
  while CheckForMutexes('{#MyAppMutex}') do
  begin
    if MsgBox('帮帮 正在运行，请先退出它再卸载。' + #13#10 + #13#10 +
              '按 Alt+X 唤出窗口，点右上角的关闭按钮即可退出。',
              mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
    Exec(ExpandConstant('{sys}\taskkill.exe'), KillCmd, '', SW_HIDE, ewWaitUntilTerminated, Rc);
    Sleep(800);
  end;

  { 装了但从没跑过就没有用户数据，别多问一句。 }
  if (not FileExists(AppDir + '\settings.json')) and
     (not FileExists(AppDir + '\crash.log')) and
     (not DirExists(AppDir + '\chats')) and
     (not DirExists(AppDir + '\images')) then
    Exit;

  { 默认按钮放在「否」上：误按回车不能删掉聊天记录。 }
  if MsgBox('是否同时删除配置、聊天记录与图片？' + #13#10 + #13#10 +
            AppDir + #13#10 + #13#10 +
            '选「否」将保留这些文件，重新安装后可以接着用。',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    RemoveUserData := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { 放在 usPostUninstall：这时卸载器已经把自己记录过的文件删完了，
     不会出现「删到一半它又要用」的竞争。 }
  if (CurUninstallStep = usPostUninstall) and RemoveUserData then
  begin
    DelTree(ExpandConstant('{app}\chats'), True, True, True);
    DelTree(ExpandConstant('{app}\images'), True, True, True);
    DeleteFile(ExpandConstant('{app}\settings.json'));
    DeleteFile(ExpandConstant('{app}\crash.log'));

    // 用户数据是卸载器唯一没记录过的东西，删掉它们之后 {app} 才变空 ——
    // 不补这一句就会永远留下一个空目录。
    // （注意：这行必须用 // 而不是 { } —— Pascal 的 { } 注释遇到 {app} 里的
    //   那个右花括号会提前闭合，后半句会被当成代码。）
    DelTree(ExpandConstant('{app}'), True, True, True);
  end;
end;
