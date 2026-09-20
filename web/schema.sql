-- ===========================================================================
-- 共享 D1 数据库 `softwares` 表结构
--
-- 整个 Cloudflare 账号只需执行一次（所有软件共用同一个库，不要重复建库）：
--   wrangler d1 execute softwares --remote --file=web/schema.sql
--
-- 【本表已存在，无需再执行】liangbuliang 官网先建过一次，内容是各软件共享的。
-- 这个文件留在这里是为了让「另起一个账号从零接入」这件事有据可依。
--
-- 计数按 app 列隔离，每个软件对应 _lib/site.js 里的 SITE.app：
--   ('banggang', 'direct_with_runtime',    500)  本站直接下载（自带运行时版）
--   ('banggang', 'direct_without_runtime', 300)  本站直接下载（框架依赖版）
--   ('banggang', 'github',                  12)  GitHub release 附件下载总量
--   ('banggang', 'github_updated_at',      ...)  上次拉取 GitHub 的时间戳（秒）
--
-- 无需手工插入初始行：首次下载 / 首次访问统计接口时会自动建行(UPSERT)。
-- ===========================================================================

CREATE TABLE IF NOT EXISTS counters (
  app   TEXT    NOT NULL,  -- 软件标识，对应 _lib/site.js 的 SITE.app
  key   TEXT    NOT NULL,  -- 'direct_<variant>' | 'github' | 'github_updated_at'
  value INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (app, key)
) WITHOUT ROWID;
