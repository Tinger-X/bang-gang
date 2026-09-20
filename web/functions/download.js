/* ==========================================================================
   /download?variant=<id> — 本站直接下载

   计数（D1 counters 中本档的 direct_<id> +1）后，从 R2 流式返回发布物。

   发布物不作为公开静态资源，所以直接下 /assets/*.exe 会 404，
   用户绕不开计数。档位与扩展名见 _lib/site.js，
   版本与文件名来自 R2 对象元数据（见 _lib/artifact.js），更新版本无需改此文件。
   ========================================================================== */

import { SITE, DEFAULT_VARIANT, findVariant } from './_lib/site';
import { artifactKey } from './_lib/artifact';

export async function onRequest(context) {
  const { env, request } = context;

  // 仅 GET 计为一次下载（HEAD 不计数）。
  if (request.method !== 'GET') {
    return new Response('Method Not Allowed', { status: 405, headers: { Allow: 'GET' } });
  }

  const requested = new URL(request.url).searchParams.get('variant') || DEFAULT_VARIANT;
  const variant = findVariant(requested);
  if (!variant) {
    // 不静默退回默认档：写错档位却拿到一个文件，会让人以为参数是生效的。
    return new Response('Unknown variant: ' + requested, { status: 404 });
  }

  const key = artifactKey(variant.id);
  const object = await env.BUCKET.get(key);
  if (!object) {
    // 先确认发布物在，再计数 —— 否则「站点已上线、安装包还没传」这段窗口里的
    // 每次点空都会把计数推上去，而这些并不是下载。
    return new Response('Not Found', { status: 404 });
  }

  // 计数失败不阻断下载。UPSERT：新软件首次下载时自动建行，无需手工初始化。
  try {
    await env.DB.prepare(
      'INSERT INTO counters (app, key, value) VALUES (?, ?, 1) ' +
        'ON CONFLICT(app, key) DO UPDATE SET value = value + 1'
    )
      .bind(SITE.app, variant.counterKey)
      .run();
  } catch (e) {
    console.error('failed to count direct download', e);
  }

  const headers = new Headers();
  object.writeHttpMetadata(headers); // 写入 Content-Type / Content-Disposition 等元数据

  // 关键头显式兜底（版本/文件名唯一来源是 R2 元数据）
  const md = object.httpMetadata || {};
  headers.set('Content-Type', md.contentType || SITE.contentType);
  headers.set(
    'Content-Disposition',
    md.contentDisposition || 'attachment; filename="' + key.split('/').pop() + '"'
  );
  headers.set('Content-Length', String(object.size));
  // 不缓存：确保每次点击都触发 Function 计数。
  headers.set('Cache-Control', 'no-store');

  return new Response(object.body, { headers });
}
