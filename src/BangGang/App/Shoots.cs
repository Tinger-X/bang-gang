#if DEBUG
namespace BangGang;

/// <summary>
/// 离屏探针产出的图片统一放这里：**仓库根的 <c>shoots/</c>**。
///
/// 与「所有脚本放 <c>tools/</c>」是同一条规矩的两半：**脚本进 tools，图片进 shoots**。
/// 以前每张图各写各的默认位置（当前目录、输入文件旁边），跑几个探针就把仓库根
/// 撒得到处都是 png，看不出哪些是产物、哪些是人放的东西。
///
/// 以「当前工作目录」为基准而不是 exe 所在目录：探针是**人**在仓库根命令行里跑的，
/// 产物就该落在人眼前；从 <c>build/bin/...</c> 里跑则会落到那里，也无妨。
/// </summary>
internal static class Shoots
{
    /// <summary>产出目录（不存在就建）。</summary>
    public static string Dir()
    {
        string d = Path.Combine(Directory.GetCurrentDirectory(), "shoots");
        try { Directory.CreateDirectory(d); } catch { /* 建不出来就让保存那一步去报错 */ }
        return d;
    }
}
#endif
