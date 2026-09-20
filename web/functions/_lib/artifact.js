/* ==========================================================================
   发布物约定（单一版本来源）

   R2 桶 `softwares` 中固定 key `<app>/<variant id>.<ext>` 始终存该档的最新版；
   版本号与下载文件名存放在该对象的 Content-Disposition 元数据里，例如：
     attachment; filename="BangGang-Setup-0.9.17-with-runtime.exe"

   更新版本只需用 wrangler 覆盖上传该 key，并带上新的 --content-disposition，
   无需改动任何代码（见 web/tools/publish-artifacts.ps1）：
     wrangler r2 object put softwares/banggang/with-runtime.exe \
       --file <新文件> --remote \
       --content-type application/vnd.microsoft.portable-executable \
       --content-disposition 'attachment; filename="BangGang-Setup-<新版本>-with-runtime.exe"'
   ========================================================================== */

import { SITE } from './site';

/** 某个档在 R2 里的固定 key。 */
export function artifactKey(variantId) {
  return SITE.app + '/' + variantId + '.' + SITE.ext;
}

// 从 Content-Disposition 头解析文件名。
export function filenameFromContentDisposition(cd) {
  if (!cd) return null;
  const m = cd.match(/filename="?([^";]+)"?/i);
  return m ? m[1] : null;
}

/**
 * 从文件名解析版本号，**统一带 `v` 前缀**（应用内 MainForm.AppVersion 也是 v 开头，
 * 页面上的版本标签要能直接和程序里显示的对上）：
 *
 *   BangGang-Setup-0.9.17-with-runtime.exe  →  v0.9.17
 *
 * 先去掉扩展名，再按配置的前缀剥掉软件名，最后剥掉 `-<variant id>` 尾巴 ——
 * 少了最后一步会把档位名当版本号的一部分，页面就会显示成 v0.9.17-with-runtime。
 * 档位尾巴只认传入的 id，不做模糊匹配：文件名里的连字符不止一处，宽松匹配会切错。
 */
export function versionFromFilename(filename, variantId) {
  if (!filename) return null;
  let base = filename.replace(/\.[^.]+$/, '');
  if (SITE.namePrefix && base.indexOf(SITE.namePrefix) === 0) {
    base = base.slice(SITE.namePrefix.length);
  }
  if (variantId) {
    const suffix = '-' + variantId;
    if (base.length > suffix.length && base.slice(-suffix.length) === suffix) {
      base = base.slice(0, -suffix.length);
    }
  }
  if (!base) return null;
  return base.charAt(0) === 'v' ? base : 'v' + base;
}
