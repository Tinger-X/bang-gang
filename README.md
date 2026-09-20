# 帮帮（BangGang）

[English](README.en.md) | **中文**

Windows 桌面直播助手：一个**在直播、录屏、屏幕共享里完全看不见**的 AI 聊天窗口。窗口只对本机用户可见，任何捕获路径（截图工具、OBS、会议软件的屏幕共享……）拿到的画面里都没有它。

`v0.9.17` · .NET 8 + WinForms · 零第三方依赖 · [MIT 许可证](LICENSE)

---

## 它解决什么问题

直播或录屏时想查资料、翻文档、问 AI，但屏幕上的内容会原封不动地播给观众。

帮帮用一个原生 Windows 能力解决这件事：给窗口设置显示亲和性 `WDA_EXCLUDEFROMCAPTURE`（`SetWindowDisplayAffinity`，Win10 2004 起可用）。被捕获画面**不是把它涂成黑框**，而是整个窗口根本不参与合成——观众看到的是它背后的桌面，连"这里有个窗口被挡住了"都看不出来。

窗口本身就是一个完整的 LLM 聊天界面：无边框、可置顶、支持流式回复、Markdown 与数学公式渲染、多模态图片输入，以及把系统声音和麦克风实时转成文字。

## 功能

### 隐身

- 主窗口、设置浮窗、截图选框遮罩、图片查看浮层——**所有**顶层窗口都应用防录屏
- 进程级 CBT 钩子（`CaptureProtector`）拦截本线程后续创建的每个顶层窗口，颜色/文件对话框不会漏网
- 窗口每次从隐藏变可见时，先清空再重设亲和性：DWM 会在隐藏期间丢掉排除表面，导致录屏退化成黑框，而 `GetWindowDisplayAffinity` 依然返回"已排除"，靠常规检查发现不了
- **Release 构建没有任何运行期开关**，防录屏恒为开启

### 对话

- 流式回复，随时暂停
- 推理模型的思考过程单独成块显示（`reasoning_content`），折叠为「思考过程 · 6.2s」，不会混进正文发回给模型
- 被长度上限截断时明确提示，而不是假装模型就说了这么多
- 多模态：图片以 data URL 内联发送，大图先缩到 1280 长边
- 附件：拖入文件、粘贴图片、截图，气泡里显示缩略图，点击放大（滚轮以指针为锚点缩放、拖动平移、Esc 关闭）

### 渲染（全部自绘，不依赖 WebView 或第三方库）

- Markdown：标题 / 围栏代码 / 引用 / 列表 / 表格 / 分隔线，行内粗体、斜体、删除线、行内代码、链接
- 数学公式：TeX 常用子集（`\frac` `\sqrt` 上下标、求和积分、希腊字母、矩阵、分段函数、`\text`）与 MathML 两套语法，解析成同一棵语法树排版
- 解析不了的内容**原样显示**（`\foo` 就画 `\foo`），绝不抛异常吞掉整条消息

### 语音转写

- WASAPI 共享模式同时采集**系统声音（loopback）+ 麦克风**，混音后重采样为 16kHz/16bit 单声道，按帧实时送入转写接口
- 纯 P/Invoke 手写的 COM，没有音频库依赖
- 麦克风被独占或不存在时自动降级为仅系统声音，并在状态栏说明
- 两种录音方式：按住说话，或按一下开始、再按一下结束
- 转写文字直接落进输入框：已定稿的分句 + 实时中间结果，停止后还会等服务端把最后几分句回完

### 界面

- 无边框自绘，显式坐标布局，随窗口缩放实时重排
- 亮色 / 暗色 / 跟随系统，可换主色、调不透明度（50%–100%）、开关主题色边框
- 左侧栏可收起展开（时间驱动的 ease-out 动画，宽度不受帧率漂移影响）
- 会话搜索、一条会话一个文件、自动恢复上次打开的会话
- 输入框里没发出去的草稿跟着会话走：切走时留在本条，切回来还在

## 系统要求

| 项目 | 要求 |
|---|---|
| 系统 | Windows 10 2004（build 19041）或更高 / Windows 11，x64 |
| 运行时 | .NET 8 Desktop Runtime（**自带运行时的安装包不需要**） |

防录屏依赖 Win10 2004 引入的 `WDA_EXCLUDEFROMCAPTURE`；更早的 Windows 上窗口仍可运行，但会退化成"被捕获时显示为黑框"。

## 下载与安装

从 Releases 下载安装包，两个版本功能完全相同，只是运行时的打包方式不同：

| 安装包 | 体积 | 适合 |
|---|---|---|
| `BangGang-Setup-<版本>-with-runtime.exe` | ~49 MB | 目标机什么都不用装 |
| `BangGang-Setup-<版本>-without-runtime.exe` | ~2.8 MB | 已装 .NET 8 Desktop Runtime；没装的话安装器会检测出来并给下载引导 |

- **每用户安装**到 `%LOCALAPPDATA%\Programs\BangGang`，不弹 UAC
- 两个包共用同一个 `AppId`，可以互相原地覆盖升级
- 安装时会通过 Restart Manager 自动关掉正在运行的旧实例
- 卸载走标准"应用和功能"，会问一次是否连配置与聊天记录一起删（默认按钮是**否**）；静默卸载一律保留

> 应用**没有托盘图标，也没有任务栏按钮**——那是刻意的：托盘图标由 explorer 渲染，跨进程给它设防录屏会被拒绝。窗口靠 `Alt+X` 显隐，重新双击 exe 也会把已有实例带到前台。

## 快速开始

1. 启动后按 `Alt+X` 唤出 / 隐藏窗口，点左侧栏右上角的齿轮打开设置
2. **设置 → 模型接入**：选一个服务商（OpenAI / DeepSeek / 通义千问 / Kimi / 火山方舟 / 智谱 GLM / Ollama 本地 / 自定义），预设会自动带出接口地址和默认模型名，填上 API Key 即可。任何 OpenAI 兼容接口都能通过"自定义"接入
3. 同一个页面往下是**语音转文字**：选火山引擎或讯飞，按各自控制台的要求填参数
4. **设置 → 对话参数**：温度、单次回复长度上限（可设为「不限」）、系统提示词、强化信息
5. **设置 → 快捷按键**：改默认的 `Alt+X` / `Alt+C` / `Alt+V`

接口地址既能填到 `/v1`，也能直接粘完整端点，两种都会被补全正确。

## 快捷键

| 默认 | 作用 |
|---|---|
| `Alt+X` | 显示 / 隐藏主窗口 |
| `Alt+C` | 选区截屏，截图直接加入输入框（同时进剪贴板） |
| `Alt+V` | 录音转写（默认按住说话，可改为按一下开始/再按一下结束） |

三个键都可以在设置里改。`Alt+X` 在设置浮窗打开时依然可用；截图和录音在设置打开时禁用。

## 数据与隐私

所有数据都放在 **exe 同目录**（`AppContext.BaseDirectory`）：

| 路径 | 内容 |
|---|---|
| `settings.json` | 全部设置，**含 API Key** |
| `chats/<会话id>.json` | 一条会话一个文件，先写临时文件再原子改名 |
| `images/` | 截图与粘贴的图片，**只增不删**（删会话不回收图片，因为同一张图可能被多条消息引用） |
| `crash.log` | 崩溃日志 |

- **没有任何遥测**，应用不向除你自己配置的服务商之外的任何服务器发请求
- 聊天内容与截图**只发给**你在设置里指定的那家 API
- API Key 以**明文**存在本机 `settings.json` 里，没有加密——请自行保管这份文件
- 附件的图片会随请求内联发送；多模态关闭或不支持时，只发送一行 `[图片] 文件名` 的说明文字

## 从源码构建

需要 .NET 8 SDK。

```powershell
dotnet build src/BangGang/BangGang.csproj -c Release
```

构建产物在仓库根的 `build/bin/<配置>/net8.0-windows/`（不在 `src/` 下，见 `Directory.Build.props`）。

### 要看界面必须用 Debug 构建

Release 构建的窗口对截屏完全不可见，屏幕抓图只会拿到桌面。Debug 构建在设置了环境变量后才允许被截图：

```powershell
$env:BANGGANG_SHOW_IN_CAPTURE = "1"
dotnet build src/BangGang/BangGang.csproj -c Debug
```

### 离屏渲染（仅 Debug）

把一段 markdown 渲染成 PNG，**不开窗口**，走的是和界面完全相同的排版/绘制路径：

```powershell
$env:BANGGANG_RENDER_MD = "sample.md"
$env:BANGGANG_RENDER_PNG = "out.png"     # 可选，默认同目录 out.png
$env:BANGGANG_RENDER_W = "620"           # 可选，正文宽度
$env:BANGGANG_RENDER_DARK = "1"          # 可选，暗色
$env:BANGGANG_RENDER_DUMP = "1"          # 可选，1=打行高/基线，2=连公式每一笔都打
```

它在单实例互斥体之前返回，因此不会被"已经开着一个帮帮"挡住。公式排得对不对和窗口、桌面都没关系——**桌面挂掉时这是唯一还能验证渲染的入口**。

## 打包安装包

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

一条命令产出两个安装包到 `dist/installer/`。脚本会：

1. 从 `BangGang.csproj` 读版本号，**并核对 `MainForm.AppVersion` 一致**（不一致直接报错——那两个数靠手改两处，漂了的表现是"安装包写着 0.9.17、程序里显示 v0.9.16"）
2. 分别 `dotnet publish` 自包含 / 框架依赖两个版本到 `build/publish/`
3. 调 `ISCC.exe` 编译 `installer/BangGang.iss`，出两个 Setup.exe 并打印 SHA256

常用参数：`-Flavor SelfContained`（只出一个）、`-SkipPublish`（复用已有产物）、`-FetchToolchain`（自动获取便携版 Inno Setup 到 `.local/innosetup/`）。

图标由 `tools/make-icon.ps1` 从 `assets/icon-char.png` 生成多尺寸 ICO，不要手改 `assets/app.ico`。

## 项目结构

单项目、单命名空间，`src/BangGang/` 下按模块分目录（文件夹只做物理聚类）：

| 目录 | 放什么 |
|---|---|
| `App/` | 应用外壳：`MainForm`（拆成 `Layout` / `Sidebar` / `Chat` / `Capture` / `Hotkeys` / `Recording` 六个 partial）、自绘外框与顶栏、品牌块、欢迎页、离屏渲染 |
| `Ui/` | 通用绘制与控件工具箱：`Ui` / `IconButton` / `Win32` / `Theme` / `Rounded` / `Fonts` / `Icons` |
| `Chat/` | 对话区：`ChatView`（消息区**唯一的绘制者**）、气泡、Markdown 渲染、公式（`MathTex` / `MathMl` / `MathLayout`）、看图浮层、会话列表、输入区、搜索框、数据模型与落盘 |
| `Settings/` | 设置浮窗（+ `Backdrop` / `Layout` 两个 partial）、`AppSettings` 持久化模型、`Pages/`（四个设置页）、`Widgets/`（只在设置界面里用的控件） |
| `Capture/` | 防录屏 affinity、截图、截图浮层 |
| `Audio/` | WASAPI 采集（纯 P/Invoke）+ 实时听写管线：`WasapiSink`（单路采集泵）→ `LiveDictation`（混流/重采样/分帧发送） |
| `Providers/` | LLM / STT 供应商预设与协议实现 |

根目录只留 `Program.cs`（入口）与 `Trace.cs`（诊断日志）。`.csproj` 是 SDK 风格、没有显式 `<Compile>` 项，新增目录自动纳入编译。

## 几个不显然的设计取舍

- **不做托盘。** 托盘图标由 explorer 渲染，跨进程给它的 `ToolbarWindow32` 设 affinity 会被拒绝（err=5），做不到逐图标防录屏。所以干脆取消托盘，退出走自绘的关闭按钮。
- **不加 NuGet 依赖。** 音频那块是纯 P/Invoke 手写的 WASAPI，HTTP 与 JSON 只用 BCL，SSE 自己按行拆。整个应用零第三方依赖。
- **气泡不是控件。** `ChatView` 把整块消息区一次画完，`MessageBubble` 不继承 `Control`。原因是子窗口不在父控件的双缓冲里——父面板重画时先铺底色、子窗口随后才被系统逐个 blit 上去，中间那一瞬就是侧栏动画里看到的破损。代价是探针不能再靠 `EnumChildWindows` 数气泡了，改读 Debug 下的 `ui-rows.json`。
- **不改鼠标指针。** 全应用鼠标一律是箭头，可点性靠悬停态和提示文字表达。
- **布局用显式坐标。** 全部由 `ApplyLayout` 从当前客户区尺寸算出，避免 `Dock` 的解析歧义。
- **`TreatWarningsAsErrors`。** 工程本来就是 0 警告，这条是把它钉住。

## 已知限制

- 防录屏作用于 Windows 的窗口捕获路径。用摄像头翻拍屏幕、采集卡这类物理旁路当然不在此列。
- 数学公式只覆盖常用子集：自定义宏、`\def`、字体包、`align` 环境、化学式不做；不认识的命令原样画出。
- Markdown 不解析 HTML；代码块超宽时硬折行，不做横向滚动。
- `images/` 只增不删，长期使用需要手工清理。
- 实时转写只内置火山引擎与讯飞两家——它们走的是各自的私有 WebSocket 协议，不存在"自定义地址"档位。

## 许可证

[MIT](LICENSE) © 2026 Tinger
