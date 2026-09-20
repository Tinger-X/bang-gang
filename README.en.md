# BangGang (帮帮)

**English** | [中文](README.md)

A Windows desktop companion for live streamers: an AI chat window that is **completely invisible to screen capture** — screen recording, streaming, and screen sharing. Only the person sitting at the machine can see it; every capture path (Snipping Tool, OBS, meeting-software screen share, …) gets nothing.

`v0.9.17` · .NET 8 + WinForms · zero third-party dependencies · [MIT License](LICENSE)

---

## What it solves

You're streaming or recording your screen and you want to look something up — but everything on your display goes straight to the audience.

BangGang uses a native Windows capability for this: window display affinity `WDA_EXCLUDEFROMCAPTURE` (`SetWindowDisplayAffinity`, available since Windows 10 2004). The captured frame doesn't show a black box where the window was — the window simply isn't part of the composition at all. The audience sees whatever is behind it, with no hint that anything is being covered.

The window itself is a full LLM chat client: borderless, always-on-top, streaming replies, Markdown and math rendering, multimodal image input, and live speech-to-text from both system audio and your microphone.

## Features

### Stealth

- The main window, the settings overlay, the screenshot selection mask and the image viewer — **every** top-level window gets the exclusion affinity
- A process-wide CBT hook (`CaptureProtector`) catches every top-level window the UI thread creates afterwards, so color/file dialogs don't slip through
- Each time the window goes from hidden to visible, the affinity is cleared and re-applied: DWM drops the exclusion surface while the window is hidden, which degrades capture to a black box — while `GetWindowDisplayAffinity` still reports "excluded", so a routine check can't detect it
- **Release builds have no runtime switch.** Capture exclusion is always on.

### Chat

- Streaming replies, pausable at any time
- Reasoning-model output (`reasoning_content`) is shown in its own collapsible block — "reasoning · 6.2s" — and is never fed back to the model as conversation content
- Replies truncated by the token limit are flagged, rather than pretending the model simply stopped there
- Multimodal: images are inlined as data URLs; large images are downscaled to a 1280px long edge first
- Attachments: drag in files, paste images, take a screenshot. Thumbnails appear in the bubble; click to open full-screen (wheel zoom anchored at the pointer, drag to pan, Esc to close)

### Rendering (all hand-drawn — no WebView, no third-party library)

- Markdown: headings, fenced code, blockquotes, lists, tables, rules; inline bold, italic, strikethrough, code, links
- Math: a common subset of TeX (`\frac`, `\sqrt`, super/subscripts, sums and integrals, Greek letters, matrices, piecewise, `\text`) plus MathML — both parse into the same syntax tree for layout
- Anything unparseable is **rendered verbatim** (`\foo` draws as `\foo`). It never throws and never swallows a whole message.

### Speech-to-text

- Captures **system audio (loopback) and microphone** simultaneously in WASAPI shared mode, mixes, resamples to 16 kHz / 16-bit / mono, and streams it to the transcription service frame by frame
- Hand-written COM via pure P/Invoke — no audio library dependency
- Falls back to system-audio-only when the microphone is exclusively held or absent, and says so in the status bar
- Two recording modes: hold-to-talk, or press once to start and again to stop
- Transcribed text lands directly in the input box: finalized sentences plus live interim results. Stopping waits for the service to flush its final sentences.

### Interface

- Borderless and fully self-drawn, explicit-coordinate layout that reflows live as the window is resized
- Light / dark / follow-system themes, configurable accent color, window opacity (50%–100%), optional accent window border
- Collapsible sidebar with a time-driven ease-out animation (width doesn't drift with frame rate)
- Conversation search, one file per conversation, last-open conversation restored on launch
- Unsent drafts follow their conversation: switch away and it stays with that chat, switch back and it's still there

## Requirements

| | |
|---|---|
| OS | Windows 10 2004 (build 19041) or later / Windows 11, x64 |
| Runtime | .NET 8 Desktop Runtime (not needed with the self-contained installer) |

Capture exclusion depends on `WDA_EXCLUDEFROMCAPTURE`, introduced in Windows 10 2004. On older Windows the app still runs, but capture degrades to "shows as a black box".

## Download & install

Grab an installer from the official site at **https://bang-gang.tin.edu.kg**, or from [Releases](https://github.com/Tinger-X/bang-gang/releases). Both builds are functionally identical — they differ only in how the runtime is packaged:

| Installer | Size | For |
|---|---|---|
| `BangGang-Setup-<version>-with-runtime.exe` | ~49 MB | Target machine needs nothing preinstalled |
| `BangGang-Setup-<version>-without-runtime.exe` | ~2.8 MB | .NET 8 Desktop Runtime already present; the installer detects it if missing and offers a download link |

- **Per-user install** into `%LOCALAPPDATA%\Programs\BangGang`, no UAC prompt
- Both packages share one `AppId`, so they upgrade over each other in place
- Setup uses Restart Manager to close any running instance automatically
- Uninstall goes through the standard "Apps & features" flow and asks once whether to delete settings and chat history (the default button is **No**); silent uninstalls always keep them

> The app has **no tray icon and no taskbar button** — deliberately. Tray icons are rendered by explorer, and setting capture affinity on its cross-process `ToolbarWindow32` is refused (err=5), so per-icon exclusion isn't achievable. Use `Alt+X` to show/hide the window; launching the exe again also brings the existing instance to the front.

## Quick start

1. Launch, press `Alt+X` to show/hide the window, then click the gear at the top-right of the sidebar to open Settings
2. **Settings → Model access**: pick a provider (OpenAI / DeepSeek / Qwen / Kimi / Volcengine Ark / Zhipu GLM / local Ollama / Custom). The preset fills in the base URL and a default model name; you supply the API key. Any OpenAI-compatible endpoint works through "Custom".
3. Further down the same page is **Speech-to-text**: pick Volcengine or iFlytek and fill in the parameters its console gives you
4. **Settings → Chat parameters**: temperature, max reply length (can be set to "unlimited"), system prompt, reinforcement prompt
5. **Settings → Shortcuts**: rebind the default `Alt+X` / `Alt+C` / `Alt+V`

The base URL can be entered either as `/v1` or as the full endpoint — both are normalized correctly.

## Shortcuts

| Default | Action |
|---|---|
| `Alt+X` | Show / hide the main window |
| `Alt+C` | Region screenshot; the image goes straight into the input box (and the clipboard) |
| `Alt+V` | Record & transcribe (hold-to-talk by default; can be switched to press-to-start/stop) |

All three are rebindable. `Alt+X` still works while the settings overlay is open; screenshot and recording are disabled there.

## Data & privacy

Everything lives **next to the executable** (`AppContext.BaseDirectory`):

| Path | Contents |
|---|---|
| `settings.json` | All settings, **including API keys** |
| `chats/<id>.json` | One file per conversation, written to a temp file then atomically renamed |
| `images/` | Screenshots and pasted images — **append-only** (deleting a conversation doesn't reclaim its images, since one image may be referenced by several messages) |
| `crash.log` | Crash log |

- **No telemetry.** The app talks to no server other than the providers you configure.
- Chat content and screenshots go **only** to the API you selected.
- API keys are stored **in plain text** in the local `settings.json` — no encryption. Keep that file safe.
- Attachment images are inlined into the request. When multimodal is off or unsupported, only a one-line `[image] filename` note is sent instead.

## Building from source

Requires the .NET 8 SDK.

```powershell
dotnet build src/BangGang/BangGang.csproj -c Release
```

Output lands in `build/bin/<Configuration>/net8.0-windows/` at the repository root (not under `src/` — see `Directory.Build.props`).

### To see the UI you need a Debug build

A Release build's window is invisible to screen capture; a screen grab returns only the desktop. Debug builds allow capture, but only after an environment variable is set:

```powershell
$env:BANGGANG_SHOW_IN_CAPTURE = "1"
dotnet build src/BangGang/BangGang.csproj -c Debug
```

### Offline rendering (Debug only)

Render a chunk of Markdown to a PNG **without opening a window**, through exactly the same layout and paint path the UI uses:

```powershell
$env:BANGGANG_RENDER_MD = "sample.md"
$env:BANGGANG_RENDER_PNG = "out.png"     # optional, defaults to out.png alongside
$env:BANGGANG_RENDER_W = "620"           # optional, body width
$env:BANGGANG_RENDER_DARK = "1"          # optional, dark theme
$env:BANGGANG_RENDER_DUMP = "1"          # optional, 1 = dump line heights/baselines, 2 = every math stroke
```

It returns before the single-instance mutex, so it isn't blocked by an already-running instance. Whether a formula typesets correctly has nothing to do with the window or the desktop — **when the desktop is unavailable, this is the only remaining way to verify rendering**.

## Building an installer

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

One command produces both installers in `dist/installer/`. The script:

1. Reads the version from `BangGang.csproj` and **verifies it matches `MainForm.AppVersion`** (a mismatch aborts the build — those two numbers are hand-edited in two places, and when they drift the installer is labelled 0.9.17 while the app reports v0.9.16)
2. Runs `dotnet publish` twice (self-contained and framework-dependent) into `build/publish/`
3. Invokes `ISCC.exe` on `installer/BangGang.iss`, producing the two Setup executables and printing their SHA256 hashes

Useful flags: `-Flavor SelfContained` (build just one), `-SkipPublish` (reuse existing publish output), `-FetchToolchain` (fetch a portable Inno Setup into `.local/innosetup/`).

The icon is generated from `assets/icon-char.png` by `tools/make-icon.ps1` into a multi-size ICO — don't hand-edit `assets/app.ico`.

## Project layout

Single project, single namespace; `src/BangGang/` is split into folders for physical clustering only:

| Folder | Contents |
|---|---|
| `App/` | Application shell: `MainForm` (split into six partials: `Layout` / `Sidebar` / `Chat` / `Capture` / `Hotkeys` / `Recording`), self-drawn frame and chrome bar, brand block, welcome view, offline rendering |
| `Ui/` | Shared drawing and widget toolkit: `Ui` / `IconButton` / `Win32` / `Theme` / `Rounded` / `Fonts` / `Icons` |
| `Chat/` | Conversation pane: `ChatView` (the **sole painter** of the message area), bubbles, Markdown rendering, math (`MathTex` / `MathMl` / `MathLayout`), image viewer, conversation list, input panel, search field, models and persistence |
| `Settings/` | Settings overlay (+ `Backdrop` / `Layout` partials), the `AppSettings` persistence model, `Pages/` (four settings pages), `Widgets/` (controls used only by the settings UI) |
| `Capture/` | Capture-exclusion affinity, screenshot capture, selection overlay |
| `Audio/` | WASAPI capture (pure P/Invoke) plus the live dictation pipeline: `WasapiSink` (one capture pump) → `LiveDictation` (mix / resample / frame) |
| `Providers/` | LLM and STT provider presets and protocol implementations |

The root holds only `Program.cs` (entry point) and `Trace.cs` (diagnostics). The `.csproj` is SDK-style with no explicit `<Compile>` items — new folders are picked up automatically.

## Non-obvious design decisions

- **No tray icon.** Tray icons are rendered by explorer, and setting affinity on its cross-process `ToolbarWindow32` is refused (err=5) — per-icon capture exclusion isn't possible. So there is no tray, and quitting goes through the self-drawn close button.
- **No NuGet dependencies.** The audio stack is hand-written WASAPI P/Invoke; HTTP and JSON use only the BCL; SSE is parsed line by line. The whole app has zero third-party dependencies.
- **Bubbles are not controls.** `ChatView` paints the entire message area in one pass and `MessageBubble` doesn't derive from `Control`. Reason: child HWNDs aren't part of the parent's double buffer — the parent fills its background first and the children are blitted in afterwards one by one, and that gap is exactly the tearing visible during the sidebar animation. The cost: probes can no longer count bubbles via `EnumChildWindows` and read Debug-only `ui-rows.json` instead.
- **The mouse cursor is never changed.** Every cursor in the app is the arrow; affordance comes from hover states and labels.
- **Explicit-coordinate layout.** Everything is computed by `ApplyLayout` from the current client size, avoiding `Dock`'s resolution ambiguity.
- **`TreatWarningsAsErrors`.** The project already builds with zero warnings; this keeps it that way.

## Known limitations

- Capture exclusion works on Windows' window-capture paths. Physical bypasses — filming the screen with a camera, a capture card — are obviously unaffected.
- Math covers a common subset only: no custom macros, `\def`, font packages, `align` environments, or chemistry. Unrecognized commands render verbatim.
- Markdown doesn't parse HTML; over-wide code blocks hard-wrap instead of scrolling horizontally.
- `images/` is append-only and needs manual cleanup over time.
- Live transcription ships with Volcengine and iFlytek only — each speaks its own private WebSocket protocol, so there is no "custom endpoint" slot for it.

## License

[MIT](LICENSE) © 2026 Tinger
