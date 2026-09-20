[English](./README.en.md)

# 帮帮官网（web/）

帮帮（BangGang）官网：纯静态站点 + Cloudflare Pages Functions。
线上地址 https://bang-gang.tin.edu.kg

源码仓库：https://github.com/Tinger-X/bang-gang

## 目录结构

```
web/
├── index.html            # 首页（单页）
├── assets/
│   ├── styles.css        # 样式（配色取自应用自己的调色板）
│   ├── script.js         # 交互：i18n / 主题 / 对比滑杆 / 下载统计
│   ├── logo.png          # 应用图标（由 tools/make-logo.ps1 生成）
│   ├── apple-touch-icon.png / favicon-32.png / favicon.svg
│   └── demo/             # 对比截图，见下面「对比截图」
├── functions/            # Pages Functions
│   ├── download.js       # /download?variant=…：计数后从 R2 流式返回安装包
│   ├── api/stats.js      # /api/stats：下载统计与版本
│   └── _lib/
│       ├── site.js       # ★ 站点配置（软件标识 / 仓库 / 档位）
│       └── artifact.js   # 发布物 key 与版本解析
├── tools/
│   ├── publish-artifacts.ps1    # 把 dist/installer/ 的安装包传进 R2
│   └── prepare-demo-shots.ps1   # 原始截图 → 页面对比图
├── schema.sql            # 共享 D1 建表语句（账号级，已应用，留档）
├── _headers              # 安全响应头 + /assets/* 长缓存
├── robots.txt / sitemap.xml
├── wrangler.example.jsonc  # 部署配置示例（提交用）
└── wrangler.jsonc          # 本机配置（.gitignore 忽略）
```

## 依赖资源（账号级共享，所有软件官网共用）

| 资源 | 名称 | 用途 |
|---|---|---|
| Pages | `banggang`（域名 `bang-gang.tin.edu.kg`） | 本项目站点 |
| D1 | `softwares`，表 `counters(app, key, value)` | 所有软件的下载计数，按 `app` 列隔离 |
| R2 | `softwares`，key 为 `<app>/<档位>.<ext>` | 所有软件的发布物，按目录前缀隔离 |

本项目占用：

- D1：`app = 'banggang'` 的行 —— `direct_with_runtime` / `direct_without_runtime` /
  `github` / `github_updated_at`
- R2：`banggang/with-runtime.exe`、`banggang/without-runtime.exe`

> **不要为本项目新建数据库或存储桶。** 共享资源由整个账号复用，`schema.sql` 也只需执行一次。

## 两个安装包（档位 / variant）

帮帮的发布物有两个：功能完全相同，只是 .NET 运行时的打包方式不同。
参考项目（liangbuliang）一个软件只有一个发布物，这里把「一个发布物」扩成了一组，
但**每个档位仍然落在 `<app>/` 前缀下**，共享桶的隔离约定不破：

| 档位 id | R2 key | D1 计数键 | 对应安装包 |
|---|---|---|---|
| `with-runtime` | `banggang/with-runtime.exe` | `direct_with_runtime` | `BangGang-Setup-<版本>-with-runtime.exe` |
| `without-runtime` | `banggang/without-runtime.exe` | `direct_without_runtime` | `BangGang-Setup-<版本>-without-runtime.exe` |

档位表写在 `functions/_lib/site.js` 的 `SITE.variants`，**加一档只需要往数组里加一项**，
页面与接口都从这里取。

## 前置要求

- Node.js + wrangler（`npm install -g wrangler`）
- 已登录：`wrangler login`

## 首次配置

1. 复制 `wrangler.example.jsonc` 为 `wrangler.jsonc`。
2. 共享资源的 `database_id` / `bucket_name` 已经填好，通常无需改动。

## 本地开发

在 `web/` 目录内执行：

```bash
wrangler pages dev --port 8787
```

访问 http://127.0.0.1:8787

> 本地 D1 / R2 是本地模拟（空的），`/api/stats` 会返回 500、`/download` 会 404。
> 想在本地把这两条路走通，先给本地库建表、往本地桶传一个安装包：
>
> ```bash
> wrangler d1 execute softwares --local --file=schema.sql
> wrangler r2 object put softwares/banggang/without-runtime.exe \
>   --file ../dist/installer/BangGang-Setup-0.9.17-without-runtime.exe --local \
>   --content-type application/vnd.microsoft.portable-executable \
>   --content-disposition 'attachment; filename="BangGang-Setup-0.9.17-without-runtime.exe"'
> ```

## 部署

在 `web/` 目录内执行（wrangler 会读取 `./wrangler.jsonc` 与 `./functions/`）：

```bash
wrangler pages deploy --project-name=banggang
```

自定义域名 `bang-gang.tin.edu.kg` 已经在 Pages 项目上绑好，不需要每次重新绑。
（要新建绑定的话，wrangler 没有这个子命令，走 API
`POST /accounts/{account_id}/pages/projects/banggang/domains`；**DNS 记录不会自动建**，
实测 `verification_data` 会停在 `CNAME record not set`，要自己加一条
`bang-gang` → `banggang.pages.dev` 的 CNAME（proxied）。）

> **`web/` 下的所有文件都会被公开。** `wrangler pages deploy` 是整目录上传，
> 而 `.assetsignore` 在 `wrangler pages deploy` 这条路上**不生效**（实测过：
> 加了它文件照样传，只是自己也被当成一个静态文件传上去）。所以
> `https://<域名>/README.md`、`/schema.sql`、`/wrangler.jsonc`、`/tools/*.ps1` **都真的存在**。
> 这不是机密泄漏 —— 同样的内容都在公开仓库里，`wrangler.jsonc` 里的 `database_id`
> 与提交的 `wrangler.example.jsonc` 一字不差 —— 但不显然，写在这里免得以后有人以为
> 部署目录是干净的。真要收干净，就得先把站点拷到一个临时目录再 deploy，
> 为这点收益不值得；也可以加一个 `functions/_middleware.js` 把这些路径 404 掉。


## 更新指引

### 发新版本

1. 出安装包：`powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1`
2. 传上去：`powershell -ExecutionPolicy Bypass -File web\tools\publish-artifacts.ps1`

第 2 步会把两个安装包覆盖到固定 key（`banggang/<档位>.exe`），并带上新的
`Content-Disposition`。**版本号与下载文件名只存在 R2 对象的这个元数据里**，
覆盖上传之后 `/api/stats` 与页面上的版本标签自动跟着变，不需要改任何代码。

脚本会自己从 `BangGang.csproj` 读版本号、从 `site.js` 读档位表，所以两边不会漂。
`-DryRun` 可以只看它打算做什么、不真传。

### 更新 js / css（缓存刷新）

`assets/*` 被 `_headers` 设为 `Cache-Control: immutable`（一年）。更新后**必须**修改
`index.html` 里对应的 `?v=xxx`，否则用户拿不到新内容：

```html
<link rel="stylesheet" href="/assets/styles.css?v=260920001" />
<script src="/assets/script.js?v=260920001"></script>
```

> `assets/` 下所有文件（含 `logo.png`、`favicon.svg`）都是 immutable，
> 任何改动都需要改版本号或换文件名。

### 换图标

`assets/logo.png` 等由 `tools/make-logo.ps1` 从 `assets/icon-char.png` 生成：

```powershell
powershell -ExecutionPolicy Bypass -File tools\make-logo.ps1
```

跟 `app.ico` 同源同规矩：别手改产出的 png。

## 对比截图（演示区）

首页的演示区是一条**可拖动的竖向分割线**，左右是同一台机器、同一时刻的两个视角：
左边「你的屏幕」（有帮帮窗口），右边「观众看到的」（录屏 / 截图捕获到的画面）。

每个场景要一对图，放进 `web/assets/demo/`：

```
<场景id>-visible.jpg   本机看到的
<场景id>-capture.jpg   捕获拿到的（同一个桌面，帮帮不在）
```

场景 id 由 `assets/script.js` 顶部的 `DEMO_GROUPS` 决定，当前 4 组：
`chat`（主窗口对话）/ `settings`（设置浮窗）/ `snip`（Alt+C 截图选框）/ `shot`（图片查看浮层）。

**怎么截**：

1. 原始截图放进 `shoots/demo/<场景id>-visible.png` 与 `<场景id>-capture.png`
   （`shoots/` 在 `.gitignore` 里，是纯本地产物）
2. 跑 `powershell -ExecutionPolicy Bypass -File web\tools\prepare-demo-shots.ps1`
   生成到 `web/assets/demo/`

两帧怎么来（`tools/run-capture-enabled.ps1` 就是干这个的）：

- **visible**：Debug 构建 + `BANGGANG_SHOW_IN_CAPTURE=1`。这是唯一的「让捕获看得见」
  的口子，只存在于 Debug；Release 没有任何运行期开关。
- **capture**：同一个场景，不带这个变量跑（或直接跑 Release 版）。

两帧之间除了帮帮窗口那一块，**其余必须一模一样**（壁纸、背景窗口、帮帮的位置大小）。
窗口是 `CenterScreen` + 1200×800 且不记忆位置，所以每次启动落点相同，两帧能严丝合缝对上。

> 只有成对存在的场景会出现在页面上，所以可以分批补。
> 一对都没有时，**整段演示区连同导航里的入口一起隐藏** —— 线上不会出现破图或半成品。
> 本地预览（localhost）则会显示一张写清还差哪些文件的占位卡。

## 关键实现

- **下载计数**：`total = 本站直接下载(D1) + GitHub release 附件下载(GitHub API)`
  - 本站：`/download?variant=…` 对该档在 D1 中的 `direct_<档位>` 原子 +1，再从 R2 流式返回。
    顶层 `direct` 是各档之和，`variants` 里给出各档的明细。
  - GitHub：`/api/stats` 每 15 分钟在 Cloudflare 侧拉取一次 GitHub API，写入 D1 兜底；
    拉不到就沿用上次的值，绝不阻塞页面。
- **档位不认识就 404**，不静默退回默认档 —— 参数写错却拿到一个文件，会让人以为参数生效了。
- **先确认发布物在，再计数**：否则「站点已上线、安装包还没传」这段窗口里，每次点空都会推高计数。
- 发布物不作为公开静态资源，直接下 `/assets/*.exe` 会 404，用户绕不开计数。
- `wrangler.jsonc` 需保留 `nodejs_compat`（R2 流式返回依赖 `node:stream`）。
- `compatibility_date` **别顺手往前调**：本机 wrangler 自带的 workerd 会拒绝比它新的日期，
  表现是本地 `pages dev` 直接起不来。

## 接入新软件

整套后端能力（下载计数 + 版本号 + GitHub 统计）都在 `functions/` 里，接入新软件
**不需要新建任何 Cloudflare 数据库或存储桶**。

### 1. 复制目录

新建仓库，把 `functions/`、`_headers`、`wrangler.example.jsonc`、`tools/` 复制过去
（`index.html` / `assets/` 换成新软件的落地页）。

### 2. 改 `functions/_lib/site.js`

```js
export const SITE = {
  app: 'newsoftware',                    // 唯一标识，用于 D1 app 列 + R2 目录前缀
  githubRepo: 'Tinger-X/newsoftware',    // 用于统计 release 附件下载量
  namePrefix: 'NewSoftware-',            // 发布文件名前缀，用于解析版本号
  ext: 'exe',                            // 发布物扩展名：exe / apk / zip / dmg …
  contentType: 'application/vnd.microsoft.portable-executable',
  variants: [
    { id: 'default', counterKey: 'direct_default', bytesHint: 0 },
  ],
};
```

> `app` 一旦上线不要改，改了会丢失历史计数、读不到已上传的发布物。
> 只有一个发布物时，`variants` 留一项即可，页面按档位渲染下载卡片。

### 3. 建 Pages 项目并部署

```bash
# wrangler.example.jsonc -> wrangler.jsonc，把 name 改成新项目名
wrangler pages deploy --project-name=newsoftware
```

共享资源的 `database_id` / `bucket_name` 保持原样。D1 表结构不用重建 ——
计数行会在首次下载时自动创建。

### 4. 上传首个发布物

```bash
wrangler r2 object put softwares/newsoftware/default.exe \
  --file <文件路径> --remote \
  --content-type application/vnd.microsoft.portable-executable \
  --content-disposition 'attachment; filename="NewSoftware-1.0.0-default.exe"'
```

完成后 `/api/stats` 即返回 `{ direct: 0, github: <n>, total: <n>, version: "v1.0.0", variants: {…} }`。

### 前端接入

页面只需请求 `/api/stats` 拿 `total`、`version` 与 `variants.*`，下载按钮指向
`/download?variant=<id>`，参见 `assets/script.js` 的 `loadStats()`。
