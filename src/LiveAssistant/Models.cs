namespace LiveAssistant;

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
}
