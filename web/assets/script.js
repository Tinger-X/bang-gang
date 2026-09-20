/* ==========================================================================
   帮帮 · BangGang — Official Website
   Interactions: i18n, theme, mobile nav, capture-comparison slider,
                 copy buttons, reveal, download stats
   ========================================================================== */
(function () {
  'use strict';

  /* ----------------------------- i18n ----------------------------- */
  var I18N = {
    zh: {
      'a11y.skip': '跳到主要内容',

      'nav.features': '功能',
      'nav.demo': '演示',
      'nav.download': '下载',
      'nav.guide': '快速开始',
      'nav.privacy': '隐私',
      'nav.cta': '下载',

      'hero.badge': '{version} · 免费 · 开源 · MIT',
      'hero.title': '帮帮',
      'hero.tagline': '一个在直播、录屏和屏幕共享里完全看不见的 AI 聊天窗口。',
      'hero.desc': '窗口只对本机用户可见。任何捕获路径拿到的画面里都没有它——不是把它涂成黑框，而是它根本不参与合成。',
      'hero.cta_download': '下载安装',
      'hero.cta_demo': '看看区别',
      'hero.stat_os': '系统要求',
      'hero.stat_deps': '第三方依赖',
      'hero.stat_deps_val': '0',
      'hero.stat_hotkey': '一键显隐',
      'hero.stat_downloads': '累计下载',
      'hero.chip_capture': '录屏里看不见',

      'features.eyebrow': '核心能力',
      'features.title': '该出现的地方出现，该消失的地方消失',
      'features.sub': '屏幕上的东西会原封不动地播给观众。帮帮用一套原生 Windows 能力把这件事反过来。',
      'features.f1_title': '对捕获完全不可见',
      'features.f1_desc': '靠窗口显示亲和性 WDA_EXCLUDEFROMCAPTURE。捕获画面里不是一块黑框——观众看到的是它背后的桌面，连「这里被挡了」都看不出来。',
      'features.f2_title': '每一个窗口都覆盖',
      'features.f2_desc': '主窗口、设置浮窗、截图选框遮罩、图片查看浮层，全部防录屏。进程级 CBT 钩子兜住后续创建的顶层窗口，颜色和文件对话框也不会漏。',
      'features.f3_title': '流式对话与思考过程',
      'features.f3_desc': '回复逐字流式输出、随时可暂停。推理模型的思考过程单独成块并折叠成「思考过程 · 6.2s」，不会混进正文发回给模型。',
      'features.f4_title': 'Markdown 与公式自绘',
      'features.f4_desc': '标题、代码块、表格、行内样式，以及 TeX 与 MathML 两套语法的常用公式，全部自己排版绘制。不套 WebView，不引第三方库；解析不了的内容原样显示，绝不吞掉整条消息。',
      'features.f5_title': '图片随手丢进来',
      'features.f5_desc': '拖入文件、粘贴图片，或者按 Alt+C 直接截图，都会作为多模态输入发给模型。气泡里显示缩略图，点开可放大——滚轮以指针为锚点缩放、拖动平移。',
      'features.f6_title': '系统声音 + 麦克风实时转写',
      'features.f6_desc': 'WASAPI 共享模式同时采集系统回放和麦克风，混音重采样后按帧送进转写接口，文字直接落进输入框。麦克风被独占或不存在时自动降级为只录系统声音。',

      'demo.eyebrow': '眼见为实',
      'demo.title': '拖动中间那条线',
      'demo.sub': '左右是同一台机器、同一时刻的两个视角。左边是你看到的，右边是观众看到的。',
      'demo.tag_you': '你的屏幕',
      'demo.tag_audience': '观众看到的',
      'demo.handle': '对比分割线：左右拖动，或用方向键',
      'demo.pending': '对比截图还没补上。把成对的截图放进 web/assets/demo/ 后，这一段会自动出现（线上同理，缺图时整段隐藏）。',
      'demo.alt_you': '本机看到的屏幕：帮帮窗口浮在桌面上',
      'demo.alt_audience': '录屏 / 截图捕获到的画面：同一个桌面，帮帮窗口不存在',

      'download.eyebrow': '获取应用',
      'download.title': '下载帮帮',
      'download.sub': '当前最新版本 {version}。两个安装包功能完全相同，只是 .NET 运行时的打包方式不同。',
      'download.with_title': '自带运行时',
      'download.with_size': '约 49 MB',
      'download.with_desc': '目标机什么都不用装，下载完双击就能用。不确定自己装没装 .NET 运行时的话，选这个。',
      'download.without_title': '依赖已装运行时',
      'download.without_size': '约 2.8 MB',
      'download.without_desc': '体积小得多，前提是本机已经装了 .NET 8 桌面运行时。没装的话安装器会检测出来，并给你下载引导。',
      'download.btn': '官网直连下载',
      'download.count_unit': '次下载',
      'download.size_fmt': '约 {n} MB',
      'download.cta_github': '从 GitHub Releases 下载',
      'download.cta_star': '去 GitHub 标星',
      'download.note1': '每用户安装到 <code>%LOCALAPPDATA%\\Programs\\BangGang</code>，不弹 UAC。',
      'download.note2': '两个安装包共用同一个 AppId，可以互相原地覆盖升级。',
      'download.note3': '安装时会自动关掉正在运行的旧实例；卸载走标准「应用和功能」。',
      'download.note4': '卸载时会问一次是否连配置和聊天记录一起删，默认按钮是「否」；静默卸载一律保留。',
      'download.note': '安装包由 Tinger 构建，构建过程与源码完全公开，可在 GitHub 仓库中审计。',

      'guide.eyebrow': '快速开始',
      'guide.title': '三步就能用起来',
      'guide.sub': '帮帮没有托盘图标，也没有任务栏按钮——这是刻意的，见下面的常见问题。',
      'guide.s1_title': '按 Alt+X 唤出窗口',
      'guide.s1_desc': '启动后是隐藏状态。Alt+X 显示或隐藏主窗口，重新双击 exe 也会把已有实例带到前台。',
      'guide.s2_title': '接上模型',
      'guide.s2_desc': '点左上角的齿轮打开设置 →「模型接入」，选一个服务商，预设会带出接口地址和默认模型名，填上 API Key 即可。任何 OpenAI 兼容接口都能通过「自定义」接入，地址填到 /v1 或直接粘完整端点都会被补全正确。',
      'guide.s3_title': '开始对话',
      'guide.s3_desc': '拖入文件、粘贴图片或按 Alt+C 截图，按 Alt+V 把系统声音和麦克风实时转成文字。设置里还能改快捷键、温度、回复长度上限与系统提示词。',
      'guide.col_key': '默认快捷键',
      'guide.col_action': '作用',
      'guide.hk1': '显示 / 隐藏主窗口',
      'guide.hk2': '选区截屏，截图直接进输入框（同时进剪贴板）',
      'guide.hk3': '录音转写（按住说话，可改成按一下开始 / 再按一下结束）',
      'guide.hk_note': '三个键都可以在设置里改。Alt+X 在设置浮窗打开时依然可用；截图和录音在设置打开时禁用。',
      'guide.tech_title': '内置的模型服务商',
      'guide.tech_desc': 'OpenAI、DeepSeek、通义千问、Kimi、火山方舟、智谱 GLM、Ollama 本地，以及自定义。语音转写内置火山引擎与讯飞两家。',
      'guide.zero_title': '零第三方依赖',
      'guide.zero_desc': 'HTTP、JSON、SSE 全部用 .NET 基础库自己处理，音频是纯 P/Invoke 手写的 WASAPI。整个应用没有引用任何一个 NuGet 包。',
      'guide.build_title': '从源码构建',
      'guide.code_label': '构建命令',
      'guide.copy': '复制',
      'guide.copied': '已复制',
      'guide.build_note': '需要 .NET 8 SDK。构建产物在仓库根的 build/bin/ 下。',

      'privacy.eyebrow': '数据与隐私',
      'privacy.title': '数据全在本机，但有一件事你得知道',
      'privacy.sub': '帮帮没有任何遥测，只和你自己配置的那家模型服务商通信。',
      'privacy.col_path': '路径（exe 同目录）',
      'privacy.col_content': '内容',
      'privacy.r1': '全部设置，<strong>含 API Key</strong>',
      'privacy.r2': '一条会话一个文件，先写临时文件再原子改名',
      'privacy.r3': '截图与粘贴的图片，只增不删',
      'privacy.r4': '崩溃日志',
      'privacy.warn': '<strong>API Key 以明文存放在本机的 settings.json 里，没有加密。</strong>请自行保管这份文件，不要把它连同整个安装目录一起分享出去。聊天内容与截图只会发送到你在设置里指定的那家 API。',
      'privacy.note': '没有任何遥测。应用不会向除你自己配置的服务商之外的任何服务器发请求，无需注册，也没有分析或广告追踪。',

      'faq.eyebrow': '常见问题',
      'faq.title': '你可能想知道',
      'faq.q1': '它收费吗？',
      'faq.a1': '完全免费、开源，MIT 许可证。你可以自由下载使用，也可以去 GitHub 读源码、提 issue。',
      'faq.q2': '为什么录屏里完全看不到它，连黑框都没有？',
      'faq.a2': '因为它走的是窗口显示亲和性 WDA_EXCLUDEFROMCAPTURE：被捕获的画面里这个窗口根本不参与合成。观众看到的是它背后的桌面，而不是一块遮挡。',
      'faq.q3': '支持哪些 Windows 版本？',
      'faq.a3': 'Windows 10 2004（build 19041）或更高，以及 Windows 11，x64。防录屏依赖 Win10 2004 才引入的 WDA_EXCLUDEFROMCAPTURE；更早的 Windows 上帮帮仍能运行，但会退化成「被捕获时显示为黑框」。',
      'faq.q4': '为什么没有托盘图标，也没有任务栏按钮？',
      'faq.a4': '这是刻意的。托盘图标由 explorer 进程渲染，跨进程给它设置防录屏会被系统拒绝，做不到逐个图标隐藏——那样反而会漏。所以干脆取消托盘，退出走窗口上自绘的关闭按钮，唤出靠 Alt+X。',
      'faq.q5': '用摄像头翻拍屏幕能看到它吗？',
      'faq.a5': '能。防录屏作用的是 Windows 的窗口捕获路径，摄像头翻拍、采集卡这类物理旁路不在此列。这一点没办法也不打算绕开。',
      'faq.q6': '数学公式支持到什么程度？',
      'faq.a6': '覆盖常用子集：分数、根号、上下标、求和积分、希腊字母、矩阵、分段函数、\\text 等，TeX 与 MathML 两套语法解析成同一棵语法树。自定义宏、\\def、字体包、align 环境、化学式不做——不认识的命令会原样画出来，不会报错吞掉内容。',
      'faq.q7': '卸载会删掉我的聊天记录吗？',
      'faq.a7': '会问你一次，默认按钮是「否」。静默卸载（带参数、无人应答）时一律保留配置与聊天记录。images/ 目录只增不删，长期使用需要手工清理。',

      'footer.tagline': '在直播和录屏里完全看不见的 AI 聊天窗口。',
      'footer.product': '产品',
      'footer.resources': '资源',
      'footer.contact': '联系',
      'footer.faq': '常见问题',
      'footer.issues': '问题反馈 / Issues',
      'footer.rights': '保留所有权利'
    },
    en: {
      'a11y.skip': 'Skip to content',

      'nav.features': 'Features',
      'nav.demo': 'Demo',
      'nav.download': 'Download',
      'nav.guide': 'Get started',
      'nav.privacy': 'Privacy',
      'nav.cta': 'Download',

      'hero.badge': '{version} · Free · Open source · MIT',
      'hero.title': 'BangGang',
      'hero.tagline': 'An AI chat window that is completely invisible to streaming, screen recording, and screen sharing.',
      'hero.desc': 'The window is visible only to the person at the machine. No capture path ever picks it up — not as a black box, but because it never joins the composition at all.',
      'hero.cta_download': 'Download',
      'hero.cta_demo': 'See the difference',
      'hero.stat_os': 'Requires',
      'hero.stat_deps': 'Third-party deps',
      'hero.stat_deps_val': '0',
      'hero.stat_hotkey': 'Show / hide',
      'hero.stat_downloads': 'Total downloads',
      'hero.chip_capture': 'Invisible to capture',

      'features.eyebrow': 'What it does',
      'features.title': 'There when you need it, gone when you don\'t',
      'features.sub': 'Whatever is on your screen goes straight to the audience. BangGang uses a native Windows capability to reverse that.',
      'features.f1_title': 'Invisible to capture',
      'features.f1_desc': 'Built on window display affinity (WDA_EXCLUDEFROMCAPTURE). The captured frame doesn\'t show a black box — the audience sees the desktop behind it, with no hint that anything is covered.',
      'features.f2_title': 'Every window is covered',
      'features.f2_desc': 'The main window, the settings panel, the screenshot-selection mask, and the image viewer are all excluded from capture. A process-wide CBT hook catches top-level windows created later, so colour and file dialogs don\'t leak either.',
      'features.f3_title': 'Streaming replies, folded reasoning',
      'features.f3_desc': 'Replies stream in token by token and can be paused at any time. A reasoning model\'s thinking is shown in its own collapsible block ("reasoning · 6.2s") and never mixed back into the message sent to the model.',
      'features.f4_title': 'Markdown and math, drawn in-house',
      'features.f4_desc': 'Headings, code fences, tables and inline styles, plus common formulas in both TeX and MathML, are all laid out by hand. No WebView, no third-party library; anything unparsable is drawn verbatim rather than swallowing the message.',
      'features.f5_title': 'Drop an image in',
      'features.f5_desc': 'Drag in a file, paste an image, or press Alt+C to grab a screenshot — all of it goes to the model as multimodal input. Thumbnails appear in the bubble and open full-size with pointer-anchored zoom and pan.',
      'features.f6_title': 'Live transcription from system audio + mic',
      'features.f6_desc': 'WASAPI shared mode captures system loopback and your microphone together, mixes and resamples them, and streams frames to the transcription API with the text landing straight in the input box. If the mic is taken or missing it degrades to system audio only.',

      'demo.eyebrow': 'See for yourself',
      'demo.title': 'Drag the line in the middle',
      'demo.sub': 'Both sides are the same machine at the same moment. On the left is what you see; on the right is what the audience sees.',
      'demo.tag_you': 'Your screen',
      'demo.tag_audience': 'What the audience sees',
      'demo.handle': 'Comparison divider: drag, or use the arrow keys',
      'demo.pending': 'Comparison screenshots are not in yet. Drop the paired images into web/assets/demo/ and this section appears automatically (the same rule applies in production — with no images the whole section stays hidden).',
      'demo.alt_you': 'The screen as you see it: the BangGang window sits on the desktop',
      'demo.alt_audience': 'What screen capture receives: the same desktop with no BangGang window',

      'download.eyebrow': 'Get the app',
      'download.title': 'Download BangGang',
      'download.sub': 'Latest version {version}. Both installers are functionally identical — they differ only in how the .NET runtime is packaged.',
      'download.with_title': 'Runtime included',
      'download.with_size': '~49 MB',
      'download.with_desc': 'Nothing needs to be installed on the target machine — download and double-click. Pick this one if you are not sure whether .NET is already present.',
      'download.without_title': 'Runtime required',
      'download.without_size': '~2.8 MB',
      'download.without_desc': 'Much smaller, provided .NET 8 Desktop Runtime is already installed. If it isn\'t, the installer detects that and gives you a download link.',
      'download.btn': 'Direct download',
      'download.count_unit': 'downloads',
      'download.size_fmt': '~{n} MB',
      'download.cta_github': 'Download from GitHub Releases',
      'download.cta_star': 'Star on GitHub',
      'download.note1': 'Installs per-user into <code>%LOCALAPPDATA%\\Programs\\BangGang</code>. No UAC prompt.',
      'download.note2': 'Both installers share one AppId, so either can upgrade the other in place.',
      'download.note3': 'Setup closes a running instance automatically; uninstalling goes through the standard Apps & features entry.',
      'download.note4': 'Uninstalling asks once whether to delete settings and chats too — the default button is "No", and a silent uninstall always keeps them.',
      'download.note': 'The installers are built by Tinger. The build process and the source are fully public and auditable on GitHub.',

      'guide.eyebrow': 'Get started',
      'guide.title': 'Up and running in three steps',
      'guide.sub': 'BangGang has no tray icon and no taskbar button — that is deliberate. See the FAQ below.',
      'guide.s1_title': 'Press Alt+X to show the window',
      'guide.s1_desc': 'It starts hidden. Alt+X shows or hides the main window, and launching the exe again brings the existing instance to the front.',
      'guide.s2_title': 'Connect a model',
      'guide.s2_desc': 'Click the gear in the top-left to open Settings → "Model access", pick a provider (the preset fills in the endpoint and a default model name), then paste your API key. Any OpenAI-compatible endpoint works via "Custom" — a base URL ending in /v1 and a full endpoint URL are both normalised correctly.',
      'guide.s3_title': 'Start chatting',
      'guide.s3_desc': 'Drag in files, paste images or press Alt+C for a screenshot; press Alt+V to transcribe system audio and your microphone live. Settings also cover hotkeys, temperature, the reply length cap, and the system prompt.',
      'guide.col_key': 'Default shortcut',
      'guide.col_action': 'Action',
      'guide.hk1': 'Show / hide the main window',
      'guide.hk2': 'Region screenshot straight into the input box (also copied to the clipboard)',
      'guide.hk3': 'Record and transcribe (hold to talk; can be switched to press-to-start / press-to-stop)',
      'guide.hk_note': 'All three are configurable in Settings. Alt+X still works while the settings panel is open; screenshots and recording are disabled there.',
      'guide.tech_title': 'Built-in providers',
      'guide.tech_desc': 'OpenAI, DeepSeek, Qwen, Kimi, Volcengine Ark, Zhipu GLM, local Ollama, and a custom endpoint. Transcription ships with Volcengine and iFlytek.',
      'guide.zero_title': 'Zero third-party dependencies',
      'guide.zero_desc': 'HTTP, JSON and SSE are handled with the .NET base class library alone, and the audio path is WASAPI written by hand in pure P/Invoke. The app references no NuGet package at all.',
      'guide.build_title': 'Build from source',
      'guide.code_label': 'Build command',
      'guide.copy': 'Copy',
      'guide.copied': 'Copied',
      'guide.build_note': 'Requires the .NET 8 SDK. Build output lands in build/bin/ at the repository root.',

      'privacy.eyebrow': 'Data & privacy',
      'privacy.title': 'Everything stays local — with one thing you should know',
      'privacy.sub': 'BangGang has no telemetry whatsoever. It talks only to the model provider you configure yourself.',
      'privacy.col_path': 'Path (next to the exe)',
      'privacy.col_content': 'Contents',
      'privacy.r1': 'All settings, <strong>including your API key</strong>',
      'privacy.r2': 'One file per conversation, written to a temp file and atomically renamed',
      'privacy.r3': 'Screenshots and pasted images, append-only',
      'privacy.r4': 'Crash log',
      'privacy.warn': '<strong>Your API key is stored in plain text in the local settings.json — it is not encrypted.</strong> Look after that file, and don\'t share it along with the install directory. Chats and screenshots are sent only to the API you configured.',
      'privacy.note': 'No telemetry. The app makes no request to any server other than the provider you configure, requires no account, and has no analytics or ad tracking.',

      'faq.eyebrow': 'FAQ',
      'faq.title': 'Things you may wonder',
      'faq.q1': 'Does it cost anything?',
      'faq.a1': 'It is completely free and open source under the MIT licence. Download and use it freely, or read the source and file issues on GitHub.',
      'faq.q2': 'Why is it completely invisible to recording — not even a black box?',
      'faq.a2': 'Because it uses window display affinity (WDA_EXCLUDEFROMCAPTURE): the window simply is not part of the captured composition. The audience sees the desktop behind it rather than an obstruction.',
      'faq.q3': 'Which Windows versions are supported?',
      'faq.a3': 'Windows 10 2004 (build 19041) or later, and Windows 11, x64. The capture exclusion relies on WDA_EXCLUDEFROMCAPTURE, introduced in Win10 2004; on older Windows the app still runs but degrades to showing a black box when captured.',
      'faq.q4': 'Why is there no tray icon and no taskbar button?',
      'faq.a4': 'That is deliberate. Tray icons are rendered by the explorer process, and asking it cross-process to exclude an individual icon from capture is refused by the system — so hiding them one by one is impossible and would leak. The tray was dropped entirely: quit via the self-drawn close button, summon with Alt+X.',
      'faq.q5': 'Can a camera pointed at the screen see it?',
      'faq.a5': 'Yes. The exclusion applies to Windows\' window-capture path; filming the screen, capture cards and other physical bypasses are outside it. That isn\'t something the app tries to work around.',
      'faq.q6': 'How much math does it support?',
      'faq.a6': 'A common subset: fractions, roots, super/subscripts, sums and integrals, Greek letters, matrices, piecewise definitions, \\text, and more — in both TeX and MathML, parsed into one syntax tree. Custom macros, \\def, font packages, the align environment and chemistry notation are not supported: unknown commands are drawn verbatim instead of failing the whole message.',
      'faq.q7': 'Will uninstalling delete my chats?',
      'faq.a7': 'It asks once, and the default button is "No". A silent uninstall always keeps settings and chats. The images/ directory is append-only and needs manual cleanup over time.',

      'footer.tagline': 'An AI chat window that is completely invisible to streaming and screen recording.',
      'footer.product': 'Product',
      'footer.resources': 'Resources',
      'footer.contact': 'Contact',
      'footer.faq': 'FAQ',
      'footer.issues': 'Issues / feedback',
      'footer.rights': 'All rights reserved'
    }
  };

  /* Keys whose value carries inline markup (<code>/<strong>) and therefore must
     be written with innerHTML. Everything else uses textContent — the default is
     the safe one, and this list is the explicit exception. The values are our own
     static strings, never user input. */
  var HTML_KEYS = { 'download.note1': 1, 'privacy.r1': 1, 'privacy.warn': 1 };

  var STORAGE_LANG = 'bg.lang';
  var lang = localStorage.getItem(STORAGE_LANG) || 'zh';
  var appVersion = 'v0.9.17'; // 默认版本；/api/stats 返回后动态更新

  function t(key) { return (I18N[lang] && I18N[lang][key]) || ''; }

  function applyLang() {
    document.documentElement.lang = lang === 'zh' ? 'zh-CN' : 'en';
    document.querySelectorAll('[data-i18n]').forEach(function (el) {
      var key = el.getAttribute('data-i18n');
      if (I18N[lang] && I18N[lang][key] != null) {
        var text = I18N[lang][key].replace(/\{version\}/g, appVersion);
        if (HTML_KEYS[key]) el.innerHTML = text;
        else el.textContent = text;
      }
    });
    document.querySelectorAll('.lang-btn').forEach(function (btn) {
      var active = btn.getAttribute('data-lang') === lang;
      btn.classList.toggle('is-active', active);
      btn.setAttribute('aria-pressed', String(active));
    });
    document.querySelectorAll('[data-i18n-label]').forEach(function (el) {
      var key = el.getAttribute('data-i18n-label');
      if (I18N[lang] && I18N[lang][key] != null) el.setAttribute('aria-label', I18N[lang][key]);
    });
    renderCompareTexts();
    renderDownloadCount();
    renderVariantStats();
    localStorage.setItem(STORAGE_LANG, lang);
  }

  function setLang(next) {
    if (next !== 'zh' && next !== 'en') return;
    lang = next;
    applyLang();
  }

  document.querySelectorAll('.lang-btn').forEach(function (btn) {
    btn.addEventListener('click', function () {
      setLang(btn.getAttribute('data-lang'));
    });
  });

  /* ----------------------------- Theme ----------------------------- */
  var STORAGE_THEME = 'bg.theme';
  function currentTheme() {
    var saved = localStorage.getItem(STORAGE_THEME);
    if (saved === 'light' || saved === 'dark') return saved;
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  function applyTheme() {
    var theme = currentTheme();
    document.documentElement.setAttribute('data-theme', theme);
    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.setAttribute('content', theme === 'dark' ? '#17191D' : '#2F70E0');
  }
  document.getElementById('theme-toggle').addEventListener('click', function () {
    localStorage.setItem(STORAGE_THEME, currentTheme() === 'dark' ? 'light' : 'dark');
    applyTheme();
  });

  /* ----------------------------- Mobile nav ----------------------------- */
  var navToggle = document.getElementById('nav-toggle');
  var siteNav = document.querySelector('.site-nav');
  navToggle.addEventListener('click', function () {
    var open = siteNav.classList.toggle('is-open');
    navToggle.setAttribute('aria-expanded', String(open));
  });
  siteNav.querySelectorAll('a').forEach(function (a) {
    a.addEventListener('click', function () {
      siteNav.classList.remove('is-open');
      navToggle.setAttribute('aria-expanded', 'false');
    });
  });

  /* ==========================================================================
     Capture-comparison slider

     每个场景两帧，文件名固定为 <id>-visible / <id>-capture：
       -visible  本机看到的（有帮帮窗口）
       -capture  录屏 / 截图拿到的（同一个桌面，帮帮不在）
     左半显示 -visible，右半显示 -capture，中间那条竖线可以左右拖。

     加一个场景就往 DEMO_GROUPS 里加一项 —— 页面上多一个标签，别处不用改。
     可以分批补：只有图片都在的场景会出现在标签栏里。
     ========================================================================== */
  var DEMO_DIR = '/assets/demo/';
  var DEMO_GROUPS = [
    {
      id: '01',
      zh: '第一次打开', en: 'First launch',
      zh_cap: '新建对话时的欢迎页。左边是你在本机看到的，右边是同一时刻观众看到的同一个桌面。',
      en_cap: 'The welcome screen of a fresh conversation. On the left is what you see at the machine; on the right, the very same desktop as the audience receives it.'
    },
    {
      id: '02',
      zh: '设置浮窗', en: 'Settings panel',
      zh_cap: '设置浮窗同样防录屏——在这里填 API Key、改快捷键、调提示词，都不会被播出去。',
      en_cap: 'The settings panel is excluded too — keys typed here, hotkeys changed, prompts tuned: none of it reaches the broadcast.'
    },
    {
      id: '03',
      zh: '一边播一边问', en: 'Asking mid-stream',
      zh_cap: '在直播中直接问模型。推理过程单独成块折叠在正文上方，不会混进回答里。',
      en_cap: 'Asking the model mid-stream. Its reasoning sits in its own collapsed block above the answer rather than being mixed into it.'
    },
    {
      id: '04',
      zh: '公式与排版', en: 'Math and layout',
      zh_cap: '回答里的公式和 Markdown 都是自绘排版的，不套 WebView、不引第三方库。',
      en_cap: 'Formulas and Markdown are laid out by the app itself — no WebView, no third-party library.'
    }
  ];

  var compareSection = document.getElementById('demo');
  var compareFrame = document.getElementById('compare-frame');
  var compareEl = document.getElementById('compare');
  var baseImg = document.getElementById('compare-base');
  var overImg = document.getElementById('compare-over');
  var lineEl = document.getElementById('compare-line');
  var handleEl = document.getElementById('compare-handle');
  var tabsEl = document.getElementById('compare-tabs');
  var captionEl = document.getElementById('compare-caption');
  var pendingEl = document.getElementById('compare-pending');
  var pendingListEl = document.getElementById('compare-pending-list');

  var activeGroup = null;
  var pos = 50; // percent

  function isDev() {
    return /^(localhost|127\.0\.0\.1|\[::1\])$/.test(location.hostname);
  }

  // 一个场景的两帧都在才算可用。先试 .jpg（prepare-demo-shots.ps1 的产物），
  // 没有再试 .png（直接把原始截图丢进来也能用）。
  function probeGroup(group) {
    var exts = ['jpg', 'png'];
    return new Promise(function (resolve) {
      var i = 0;
      (function tryNext() {
        if (i >= exts.length) { resolve(null); return; }
        var ext = exts[i++];
        var src = { visible: DEMO_DIR + group.id + '-visible.' + ext, capture: DEMO_DIR + group.id + '-capture.' + ext };
        var loaded = 0, failed = false, settled = false;
        function done() {
          loaded++;
          if (loaded < 2 || settled) return;
          settled = true;
          if (failed) tryNext(); else resolve({ group: group, src: src });
        }
        ['visible', 'capture'].forEach(function (k) {
          var img = new Image();
          img.onload = done;
          img.onerror = function () { failed = true; done(); };
          img.src = src[k];
        });
      })();
    });
  }

  function setPos(p) {
    pos = Math.max(0, Math.min(100, p));
    overImg.style.clipPath = 'inset(0 0 0 ' + pos + '%)';
    lineEl.style.left = pos + '%';
    handleEl.setAttribute('aria-valuenow', String(Math.round(pos)));
  }

  function renderCompareTexts() {
    if (!activeGroup) return;
    handleEl.setAttribute('aria-label', t('demo.handle'));
    captionEl.textContent = lang === 'zh' ? activeGroup.zh_cap : activeGroup.en_cap;
    baseImg.alt = t('demo.alt_you');
    overImg.alt = t('demo.alt_audience');
    tabsEl.querySelectorAll('.compare-tab').forEach(function (btn) {
      var g = DEMO_GROUPS.filter(function (x) { return x.id === btn.getAttribute('data-group'); })[0];
      if (g) btn.textContent = lang === 'zh' ? g.zh : g.en;
    });
  }

  function showGroup(group, src) {
    activeGroup = group;
    baseImg.src = src.visible;
    overImg.src = src.capture;
    tabsEl.querySelectorAll('.compare-tab').forEach(function (btn) {
      var active = btn.getAttribute('data-group') === group.id;
      btn.classList.toggle('is-active', active);
      btn.setAttribute('aria-selected', String(active));
    });
    renderCompareTexts();
  }

  function renderTabs(available) {
    tabsEl.innerHTML = '';
    available.forEach(function (item) {
      var btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'compare-tab';
      btn.setAttribute('role', 'tab');
      btn.setAttribute('data-group', item.group.id);
      btn.textContent = lang === 'zh' ? item.group.zh : item.group.en;
      btn.addEventListener('click', function () { showGroup(item.group, item.src); });
      tabsEl.appendChild(btn);
    });
  }

  // 有图才显示整段（连导航里的入口一起）；一组都没有时线上整段隐藏，
  // 本地预览则显示占位说明，省得截图没到位就以为坏了。
  function setDemoVisible(visible) {
    compareSection.hidden = !visible;
    document.querySelectorAll('[data-nav-optional="demo"], [data-nav-optional-href="demo"]').forEach(function (el) {
      el.hidden = !visible;
    });
  }

  (function initCompare() {
    Promise.all(DEMO_GROUPS.map(probeGroup)).then(function (results) {
      var available = results.filter(Boolean);
      if (!available.length) {
        if (isDev()) {
          compareFrame.hidden = true;
          tabsEl.hidden = true;
          pendingEl.hidden = false;
          pendingListEl.innerHTML = '';
          DEMO_GROUPS.forEach(function (g) {
            var li = document.createElement('li');
            li.textContent = DEMO_DIR + g.id + '-visible.jpg / ' + g.id + '-capture.jpg';
            pendingListEl.appendChild(li);
          });
          setDemoVisible(true);
        }
        return;
      }
      renderTabs(available);
      showGroup(available[0].group, available[0].src);
      setPos(50);
      setDemoVisible(true);
      // 这一段是在探测完图片之后才显示的，而 .reveal 的 IntersectionObserver 早在
      // 页面加载时就观察了它 —— 那时它还藏在 hidden 的 section 里、没有布局盒，
      // 于是被记成「不相交」。解开 hidden 之后 IO 不保证补发一次，结果就是整块对比图
      // 停在 opacity:0：位置占着、内容blank，看着像图没加载出来。
      // 直接落 in-view，不赌 IO 会回心转意（IO 只会加这个类，不会摘，重复加也无害）。
      compareFrame.classList.add('in-view');
    });

    /* 拖动：按在整块对比图上就开始跟，指针捕获让鼠标移出容器也不断线。 */
    function posFromEvent(e) {
      var rect = compareEl.getBoundingClientRect();
      if (!rect.width) return pos;
      return ((e.clientX - rect.left) / rect.width) * 100;
    }
    var dragging = false;
    compareEl.addEventListener('pointerdown', function (e) {
      dragging = true;
      if (compareEl.setPointerCapture) {
        try { compareEl.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
      }
      setPos(posFromEvent(e));
      // preventDefault 会顺带吃掉聚焦，所以按在把手上时手动补一次，
      // 否则点完把手敲方向键没有任何反应。
      if (e.target === handleEl || handleEl.contains(e.target)) handleEl.focus();
      e.preventDefault();
    });
    compareEl.addEventListener('pointermove', function (e) {
      if (dragging) setPos(posFromEvent(e));
    });
    ['pointerup', 'pointercancel'].forEach(function (type) {
      compareEl.addEventListener(type, function () { dragging = false; });
    });

    /* 键盘：handle 是 role=slider，方向键微调，Home/End 回到两端。 */
    handleEl.addEventListener('keydown', function (e) {
      var step = e.shiftKey ? 10 : 2;
      var next = null;
      if (e.key === 'ArrowLeft') next = pos - step;
      else if (e.key === 'ArrowRight') next = pos + step;
      else if (e.key === 'Home') next = 0;
      else if (e.key === 'End') next = 100;
      else if (e.key === 'PageDown') next = pos - 10;
      else if (e.key === 'PageUp') next = pos + 10;
      if (next === null) return;
      e.preventDefault();
      setPos(next);
    });
  })();

  /* ----------------------------- Copy buttons ----------------------------- */
  document.querySelectorAll('.copy-btn').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var text = btn.getAttribute('data-copy');
      var done = function () {
        var original = btn.textContent;
        btn.textContent = t('guide.copied') || 'Copied';
        btn.classList.add('is-copied');
        setTimeout(function () {
          btn.textContent = original;
          btn.classList.remove('is-copied');
        }, 1800);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(done, function () { fallbackCopy(text); done(); });
      } else {
        fallbackCopy(text);
        done();
      }
    });
  });

  function fallbackCopy(text) {
    var ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand('copy'); } catch (e) { /* ignore */ }
    document.body.removeChild(ta);
  }

  /* ----------------------------- Year ----------------------------- */
  var yearEl = document.getElementById('year');
  if (yearEl) yearEl.textContent = String(new Date().getFullYear());

  /* ----------------------------- Reveal on scroll ----------------------------- */
  var revealEls = document.querySelectorAll('.reveal');
  if ('IntersectionObserver' in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          entry.target.classList.add('in-view');
          io.unobserve(entry.target);
        }
      });
    }, { threshold: 0.12 });
    revealEls.forEach(function (el) { io.observe(el); });
  } else {
    revealEls.forEach(function (el) { el.classList.add('in-view'); });
  }

  /* ----------------------------- Download count / version ----------------------------- */
  var statDownloads = document.getElementById('stat-downloads');
  var variantCounts = {};
  var variantSizes = {};

  function formatCount(n) {
    return n.toLocaleString(lang === 'zh' ? 'zh-CN' : 'en-US');
  }
  function formatSize(bytes) {
    var mb = bytes / 1048576;
    var v = mb >= 10 ? Math.round(mb) : Math.round(mb * 10) / 10;
    return t('download.size_fmt').replace('{n}', String(v));
  }
  function renderDownloadCount() {
    if (statDownloads && variantCounts.__total != null) {
      statDownloads.textContent = formatCount(variantCounts.__total);
    }
    if (!Object.keys(variantCounts).length) return;
    document.querySelectorAll('[data-variant-count]').forEach(function (el) {
      var v = variantCounts[el.getAttribute('data-variant-count')];
      if (v != null) el.textContent = formatCount(v);
    });
  }
  function renderVariantStats() {
    document.querySelectorAll('.js-version').forEach(function (el) {
      el.textContent = appVersion;
    });
    document.querySelectorAll('[data-variant-size]').forEach(function (el) {
      var bytes = variantSizes[el.getAttribute('data-variant-size')];
      if (bytes) el.textContent = formatSize(bytes);
    });
  }

  function loadStats() {
    fetch('/api/stats', { headers: { 'Accept': 'application/json' } })
      .then(function (res) {
        if (!res.ok) throw new Error('stats unavailable');
        return res.json();
      })
      .then(function (data) {
        if (typeof data.total === 'number') variantCounts.__total = data.total;
        if (data.variants) {
          Object.keys(data.variants).forEach(function (id) {
            var v = data.variants[id] || {};
            if (typeof v.direct === 'number') variantCounts[id] = v.direct;
            if (typeof v.size === 'number') variantSizes[id] = v.size;
          });
        }
        if (data.version) appVersion = data.version;
        // 版本号参与文案插值（badge / download.sub），要重渲染一遍。
        applyLang();
        renderDownloadCount();
      })
      .catch(function () {
        // 拉取失败保持占位符「—」与默认版本，不影响页面。
      });
  }

  /* ----------------------------- Init ----------------------------- */
  applyTheme();
  applyLang();
  loadStats();
})();
