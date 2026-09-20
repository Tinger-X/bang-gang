/* ==========================================================================
   /api/stats — 下载统计与版本

   返回 {
     direct, github, total,           // total = direct + github
     version,                         // 展示用，取自默认档的 R2 元数据
     variants: { <id>: { direct, version, filename, size } }
   }

   - direct：本站直接下载（D1，实时），各档分开计，顶层是各档之和。
   - github：GitHub release 附件下载总量（Cloudflare 侧每 15 分钟拉一次 GitHub API，
     成功后持久化到 D1 作为兜底；GitHub 不可达时沿用最近已知值，绝不阻塞页面）。
   - version / filename / size：来自各档 R2 对象元数据（单一版本来源）。

   软件标识 / 档位见 _lib/site.js。所有软件共用 D1 `softwares`，
   计数按 counters.app 列隔离，互不影响。
   ========================================================================== */

import { SITE } from '../_lib/site';
import { artifactKey, filenameFromContentDisposition, versionFromFilename } from '../_lib/artifact';

const GITHUB_RELEASES_URL = 'https://api.github.com/repos/' + SITE.githubRepo + '/releases';
const GITHUB_TTL = 900; // 秒，GitHub 未认证限流 60/h，节流避免超限

async function fetchGithubDownloads() {
  try {
    const res = await fetch(GITHUB_RELEASES_URL, {
      headers: {
        'User-Agent': SITE.app + '-site',
        Accept: 'application/vnd.github+json',
      },
    });
    if (!res.ok) return null;
    const releases = await res.json();
    let total = 0;
    for (const release of releases) {
      for (const asset of release.assets || []) {
        total += asset.download_count || 0;
      }
    }
    return total;
  } catch (e) {
    return null;
  }
}

// 写入本软件的单个计数项（首次自动建行）。
function upsertCounter(env, key, value) {
  return env.DB.prepare(
    'INSERT INTO counters (app, key, value) VALUES (?, ?, ?) ' +
      'ON CONFLICT(app, key) DO UPDATE SET value = excluded.value'
  )
    .bind(SITE.app, key, value)
    .run();
}

// 读一个档的 R2 元数据。失败只影响版本展示，不影响计数返回。
async function readVariantMeta(env, variant) {
  const meta = { direct: 0, version: null, filename: null, size: null };
  try {
    const artifact = await env.BUCKET.head(artifactKey(variant.id));
    if (artifact) {
      const cd = artifact.httpMetadata && artifact.httpMetadata.contentDisposition;
      meta.filename =
        filenameFromContentDisposition(cd) || artifact.key.split('/').pop();
      meta.version = versionFromFilename(meta.filename, variant.id);
      meta.size = artifact.size;
    }
  } catch (e) {
    /* ignore */
  }
  return meta;
}

export async function onRequest(context) {
  const { env } = context;
  const jsonHeaders = { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' };

  try {
    const { results } = await env.DB.prepare('SELECT key, value FROM counters WHERE app = ?')
      .bind(SITE.app)
      .all();
    const map = {};
    for (const row of results) map[row.key] = row.value;

    const now = Math.floor(Date.now() / 1000);
    let github = map.github || 0;
    const updatedAt = map.github_updated_at || 0;

    if (now - updatedAt > GITHUB_TTL) {
      const fetched = await fetchGithubDownloads();
      if (fetched != null) {
        github = fetched;
        await upsertCounter(env, 'github', github);
        await upsertCounter(env, 'github_updated_at', now);
      }
    }

    const variants = {};
    let direct = 0;
    let version = null;
    for (const variant of SITE.variants) {
      const meta = await readVariantMeta(env, variant);
      meta.direct = map[variant.counterKey] || 0;
      if (meta.size == null) meta.size = variant.bytesHint || null;
      variants[variant.id] = meta;
      direct += meta.direct;
      if (!version && meta.version) version = meta.version;
    }

    return Response.json(
      { direct, github, total: direct + github, version, variants },
      { headers: jsonHeaders }
    );
  } catch (e) {
    // D1 本身异常时的兜底（极少见）。
    return Response.json({ error: 'unavailable' }, { status: 500, headers: jsonHeaders });
  }
}
