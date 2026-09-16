namespace BangGang;

/// <summary>一段会话。</summary>
public class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新对话";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>依据首条用户消息等生成显示标题。</summary>
    public void RefreshTitle()
    {
        var firstUser = Messages.FirstOrDefault(m => m.Role == "user");
        if (firstUser != null)
        {
            string t = string.IsNullOrWhiteSpace(firstUser.Text) ? "(附件)" : firstUser.Text;
            Title = t.Trim();
            if (Title.Length > 18) Title = Title[..18] + "…";
        }
        else Title = "新对话";
        UpdatedAt = DateTime.Now;
    }
}

public class ChatMessage
{
    public string Role { get; set; } = "user"; // "user" | "assistant"
    public DateTime When { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    public List<Attachment> Attachments { get; set; } = new();
}

/// <summary>输入框/消息中的附件。</summary>
public class Attachment
{
    public string Kind { get; set; } = "file"; // "image" | "file"
    public string Name { get; set; } = "";
    public string? Path { get; set; }          // 源文件路径（录音/拖入等）
    public string? ImageDataPath { get; set; } // 截图临时文件等

    /// <summary>
    /// 文件大小（字节）。加进来的时候抓一次就**不再跟随磁盘变化** —— 卡片上那半句
    /// 「PDF · 625KB」只是给用户认文件用的，源文件事后被改 / 被删都不该让界面上的数字跳。
    /// 拿不到时是 0，此时那半句直接不画（见 <see cref="AttachTypes.MetaOf"/>）。
    /// </summary>
    public long Size { get; set; }

    public static Attachment ForImage(string name, string path) => new() { Kind = "image", Name = name, Path = path };
    public static Attachment ForFile(string name, string path) => new() { Kind = "file", Name = name, Path = path };
    public static Attachment ForClipboardImage(string name, string path) => new() { Kind = "image", Name = name, Path = path };

    /// <summary>加载图片以便展示（失败返回 null）。</summary>
    public Image? LoadImage(int maxW, int maxH)
    {
        try
        {
            if (Path == null || !System.IO.File.Exists(Path)) return null;
            using var full = Image.FromFile(Path);
            var bmp = new Bitmap(full, Scale(full.Width, full.Height, maxW, maxH));
            return bmp;
        }
        catch { return null; }
    }

    private static Size Scale(int w, int h, int maxW, int maxH)
    {
        if (w <= maxW && h <= maxH) return new Size(w, h);
        double k = Math.Min((double)maxW / w, (double)maxH / h);
        return new Size(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)));
    }

    /// <summary>
    /// 列表缩略图：按**铺满**缩放 —— 短边也至少有 <paramref name="box"/> 那么长。
    ///
    /// 和 <see cref="LoadImage"/> 的「装下」正好相反，因为用途不同：气泡里要看清整张图，
    /// 附件格子里只回答「这是哪一张」。装下的话一张 2000×200 的全景会被压成 176×17，
    /// 再铺进 44px 的方格就得放大 2.5 倍 —— 糊成一片。这里保证短边够长，由画的那一头裁。
    ///
    /// 原图本来就比格子小就不放大：那时糊是必然的，但至少不额外损失一次重采样。
    /// </summary>
    public Image? LoadThumb(int box)
    {
        try
        {
            if (Path == null || !System.IO.File.Exists(Path)) return null;
            using var full = Image.FromFile(Path);
            double k = (double)box / Math.Min(full.Width, full.Height);
            if (k >= 1) return new Bitmap(full);
            return new Bitmap(full, new Size(Math.Max(1, (int)Math.Round(full.Width * k)),
                                             Math.Max(1, (int)Math.Round(full.Height * k))));
        }
        catch { return null; }
    }
}
