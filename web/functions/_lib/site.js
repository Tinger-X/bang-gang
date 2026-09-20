/* ==========================================================================
   站点配置 —— 接入新软件时【只需要改这个文件】

   所有软件共用同一套 Cloudflare 资源（无需为每个项目新建）：
     D1  `softwares`  表 counters(app, key, value)，用 app 列隔离各软件的计数
     R2  `softwares`  用 `<app>/` 目录前缀隔离各软件的发布物

   D1 建表语句见 web/schema.sql（整个账号只需执行一次）。
   完整接入步骤见 web/README.md 的「接入新软件」章节。
   ========================================================================== */

export const SITE = {
  // 共享资源中的唯一标识：D1 的 app 列 + R2 的目录前缀。
  // 上线后请勿修改，改了会丢失历史计数、读不到已上传的发布物。
  app: 'banggang',

  // GitHub 仓库 owner/name，用于统计 release 附件下载量。
  githubRepo: 'Tinger-X/bang-gang',

  // 发布物扩展名与 Content-Type。换软件类型时改这两项（apk / dmg / zip …）。
  ext: 'exe',
  contentType: 'application/vnd.microsoft.portable-executable',

  // 发布文件名前缀，用于从 Content-Disposition 解析版本号。
  // 约定文件名形如 `<namePrefix><版本>[-<variant id>].<ext>`：
  //   BangGang-Setup-0.9.17-with-runtime.exe  →  v0.9.17
  namePrefix: 'BangGang-Setup-',

  /* ------------------------------------------------------------------------
     发布物分档（variant）

     帮帮的两个安装包功能完全相同，只是 .NET 运行时的打包方式不同，所以
     「一个软件一个发布物」在这里变成了一组。每个 variant 对应：
       R2 key     `<app>/<id>.<ext>`        例如 banggang/without-runtime.exe
       D1 计数键  `direct_<id 下划线化>`     例如 direct_without_runtime

     key 仍然落在 `<app>/` 前缀下，共享桶的隔离约定不破。

     加一档就往下加一项；页面与接口都从这里取，不用改别处。
     `hint` 是给页面用的推荐位说明（前端按 id 取自己的文案，这里只做兜底）。
     ---------------------------------------------------------------------- */
  variants: [
    {
      id: 'with-runtime',
      counterKey: 'direct_with_runtime',
      bytesHint: 52000000, // ~49 MB，给 /api/stats 读不到 R2 元数据时的兜底展示
    },
    {
      id: 'without-runtime',
      counterKey: 'direct_without_runtime',
      bytesHint: 2900000, // ~2.8 MB
    },
  ],
};

/** 默认档：不带 `?variant=` 时下这个。 */
export const DEFAULT_VARIANT = SITE.variants[0].id;

/** 按 id 找档，找不到返回 null（调用方据此 404，而不是静默退回默认档）。 */
export function findVariant(id) {
  return SITE.variants.find((v) => v.id === id) || null;
}
