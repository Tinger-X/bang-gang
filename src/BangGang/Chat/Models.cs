namespace BangGang;

/// <summary>一段会话。</summary>
public class Conversation
{
    /// <summary>
    /// 标题最多留多少个字符。超出由<b>标题栏</b>的省略号收尾（<c>AutoEllipsis</c>），
    /// 这里卡的是存进文件的长度 —— 不卡的话模型偶尔会回一整段摘要，会话列表里那一行
    /// 就只剩省略号了。
    /// </summary>
    public const int TitleMax = 30;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新对话";

    /// <summary>
    /// 这条标题已经**定稿**：用户自己改的，或者模型按首条消息总结出来的。
    /// 定稿之后 <see cref="RefreshTitle"/> 不再按首句重算 —— 否则用户起的名字会在
    /// 下一次发消息时被首句盖掉，模型起的那条也一样（首条消息还在，条件一直成立）。
    /// </summary>
    public bool TitleLocked { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// 输入框里还没发出去的那半句（正文）。
    ///
    /// **不落盘**：<see cref="ChatStore.Save"/> 只挑 Id / Title / 两个时间 / Messages /
    /// 下面那几个上下文字段写进库，这里加的字段进不去 —— 那是有意的（草稿是临时状态，
    /// 重启后照旧从头开始，和 0.9.13 之前的行为一致）。将来往 Save 里加字段时别顺手把它带上。
    ///
    /// 存在对话上而不是存在输入区里，是因为它天然属于一条对话：切走时留在本条上、切回来再
    /// 装回去（两端都在 <c>MainForm.ActivateConversation</c>），删掉这条时它跟着一起没 ——
    /// 换成一张「会话 id → 草稿」的表，就得再记一本账去记住该忘掉谁。
    /// </summary>
    public string DraftText { get; set; } = "";

    /// <summary>见 <see cref="DraftText"/>：那半句带着的附件。</summary>
    public List<Attachment> DraftFiles { get; set; } = new();

    /// <summary>
    /// 上下文压缩出来的摘要（「压缩」策略，见 <c>MainForm.Context</c>）。
    /// 空串 = 这条会话还没压缩过。
    ///
    /// **不作为一条消息存在 <see cref="Messages"/> 里**：那份列表是界面与搜索的账本
    /// （侧栏搜索遍历它、气泡按它渲染、文件工具按它找附件路径），塞一条合成消息进去，
    /// 要么得给它开气泡特例，要么它会出现在搜索结果里 —— 两头都不对。
    /// </summary>
    public string CtxSummary { get; set; } = "";

    /// <summary><see cref="CtxSummary"/> 覆盖到了第几条消息（Messages 的下标）。</summary>
    public int CtxSummaryUpto { get; set; }

    /// <summary>
    /// 上一轮请求服务端报回来的真实输入 token 数。0 = 还没拿到过。
    ///
    /// 拿它当**锚点**：下一次算「用了多少」就是「这个真实值 + 锚点之后新增的消息」，
    /// 而不是拿本地估算硬猜整段 —— 估算器只擅长算增量，误差不会随对话变长而累积。
    /// </summary>
    public int LastPromptTokens { get; set; }

    /// <summary>
    /// 锚点量到哪儿：<see cref="LastPromptTokens"/> 那一次请求发出时，
    /// <see cref="Messages"/> 有多少条。见 <c>ContextManager.Used</c>。
    /// </summary>
    public int AnchorMsgs { get; set; }

    /// <summary>
    /// 依据首条用户消息等生成显示标题。标题定稿过（见 <see cref="TitleLocked"/>）就直接返回，
    /// 但「这条会话刚刚动过」这件事照记 —— <c>UpdatedAt</c> 是会话列表的排序键，
    /// 跟标题从哪儿来没有关系。
    /// </summary>
    public void RefreshTitle()
    {
        UpdatedAt = DateTime.Now;
        if (TitleLocked) return;
        var firstUser = Messages.FirstOrDefault(m => m.Role == "user");
        if (firstUser != null)
        {
            string t = string.IsNullOrWhiteSpace(firstUser.Text) ? "(附件)" : firstUser.Text;
            Title = TidyTitle(t);
            if (Title.Length > 18) Title = Title[..18] + "…";
        }
        else Title = "新对话";
    }

    /// <summary>
    /// 把外来的一串标题（用户敲进编辑框的 / 模型总结出来的）收拾成能存、能显示的样子：
    /// 换行压成空格、连续空白并成一个、去首尾空白、按 <see cref="TitleMax"/> 截断。
    ///
    /// 空串原样返回空串 —— 「要不要接受一个空标题」是调用方的决定，不该在这里替它拿主意。
    /// </summary>
    public static string TidyTitle(string? raw)
    {
        string s = (raw ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        s = string.Join(" ", parts);
        return s.Length > TitleMax ? s[..TitleMax] : s;
    }
}

public class ChatMessage
{
    public string Role { get; set; } = "user"; // "user" | "assistant"
    public DateTime When { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";

    /// <summary>
    /// 推理模型在正文之前吐出来的思考过程（`delta.reasoning_content`，deepseek-r1 /
    /// 各种 `-thinking` 模型都有）。不是正文的一部分：**不会**被 <see cref="LlmClient"/>
    /// 发回给模型，只在气泡里当「模型正在干什么」显示 —— 思考阶段动辄几秒到几十秒，
    /// 一个字都不显示的话界面看着就是卡住了。
    /// </summary>
    public string Reasoning { get; set; } = "";

    /// <summary>这一轮思考花了多少毫秒（0 = 没有思考）。收起状态那行「思考过程 · 6.2s」用它。</summary>
    public int ReasoningMs { get; set; }

    /// <summary>
    /// 这条回复没能正常收尾时的说明，目前只有「被最大回复长度截断」。
    ///
    /// 和 <see cref="Text"/> 分开存是有意的：正文会被发回给模型当上下文，
    /// 把一句中文警告混进去，模型下一轮就会对着自己的「警告」接着往下说。
    /// 它只在气泡里显示。
    /// </summary>
    public string Warning { get; set; } = "";

    public List<Attachment> Attachments { get; set; } = new();

    /// <summary>
    /// 这一轮里模型调用过的工具（时间 / 计算 / 读文件 …），按发生顺序。
    ///
    /// **和 <see cref="Text"/> 一样会被发回模型，但只有一行摘要**：完整结果（可能是几千字的
    /// 文件内容）只在这里存着给界面展开看，回灌时用的是 <see cref="ToolCall.Brief"/>。
    /// 不这么做的话，读一次文件就把上下文吃掉一大半，聊两轮就顶到上限。
    /// </summary>
    public List<ToolCall> ToolCalls { get; set; } = new();

    /// <summary>
    /// 这条消息有没有值得留下来 / 值得画出来的东西。
    ///
    /// 落盘那边用它决定「空会话不写文件」，所以思考过程也算数：用户按了暂停、
    /// 或者模型把长度上限全用在思考上时，正文是空的而思考是满的 —— 那一轮
    /// 用户明明看见了东西，重启回来看见它没了会以为聊天记录丢了。
    ///
    /// 工具调用同理，而且后果更重：只有工具调用、正文一个字都没有的那一轮要是被判成空，
    /// 整个会话就一条可落盘的消息都不剩，<see cref="ChatStore.Save"/> 会直接**删掉会话文件**。
    ///
    /// 四个属性都可能是 JSON 里的 null（反序列化不看非空声明），所以逐个兜一层。
    /// </summary>
    public bool IsEmpty =>
        (Text ?? "").Length == 0 && (Reasoning ?? "").Length == 0
        && (Attachments?.Count ?? 0) == 0 && (ToolCalls?.Count ?? 0) == 0;
}

/// <summary>
/// 模型要求的一次工具调用，以及它的执行结果。
///
/// 字段全是公开可写的：这东西跟着 <see cref="ChatMessage"/> 一起被 System.Text.Json
/// 序列化进聊天文件（见 <see cref="ChatStore"/>），反序列化要有无参构造与可写属性。
/// </summary>
public class ToolCall
{
    /// <summary>
    /// 服务端给的调用 id（<c>call_xxx</c>）。把结果回灌时必须**原样带回** ——
    /// 用错 id 会被接口拒掉，而且各家报的错还不一样。
    /// </summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>模型给的参数 JSON 原文，如 <c>{"expr":"1+1"}</c>。展开工具那一条时显示它。</summary>
    public string Args { get; set; } = "";

    /// <summary>执行结果全文（已按上限截断过）。落盘、展开时看，**不回灌给模型**。</summary>
    public string Result { get; set; } = "";

    /// <summary>
    /// 一行摘要，如「计算 1+1」「读取 报告.docx」。
    /// 折叠行的括号里、以及**发给模型的历史**用的都是它。
    /// </summary>
    public string Brief { get; set; } = "";

    /// <summary>执行成功与否。失败时 <see cref="Result"/> 里是一句给模型看的说明。</summary>
    public bool Ok { get; set; } = true;

    /// <summary>耗时毫秒。上面那一行「工具调用 · 3 次」的括号里用它。</summary>
    public int Ms { get; set; }
}

/// <summary>输入框/消息中的附件。</summary>
public class Attachment
{
    public string Kind { get; set; } = "file"; // "image" | "file"
    public string Name { get; set; } = "";

    /// <summary>
    /// 文件路径。**两种附件这个字段的含义不同**（分界见 <see cref="AttachmentStore"/>）：
    /// 托管附件（截图 / 粘贴图）指向我们自己的 <c>attachments/&lt;sha256&gt;.png</c>；
    /// 非托管附件（用户拖进来的文件）指向**用户自己的文件**，我们从没复制过它，
    /// 所以也永远不删它。
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// 文件大小（字节）。加进来的时候抓一次就**不再跟随磁盘变化** —— 卡片上那半句
    /// 「PDF · 625KB」只是给用户认文件用的，源文件事后被改 / 被删都不该让界面上的数字跳。
    /// 拿不到时是 0，此时那半句直接不画（见 <see cref="AttachTypes.MetaTail"/>）。
    /// </summary>
    public long Size { get; set; }

    public static Attachment ForImage(string name, string path) => new() { Kind = "image", Name = name, Path = path };
    public static Attachment ForFile(string name, string path) => new() { Kind = "file", Name = name, Path = path };
    public static Attachment ForClipboardImage(string name, string path) => new() { Kind = "image", Name = name, Path = path };

    /// <summary>
    /// 列表缩略图：按**铺满**缩放 —— 短边也至少有 <paramref name="box"/> 那么长。
    ///
    /// 气泡里要看清整张图、附件格子里只回答「这是哪一张」，用途不同所以缩放方式也不同：
    /// 按「装下」缩的话，一张 2000×200 的全景会被压成 176×17，再铺进 44px 的方格
    /// 就得放大 2.5 倍 —— 糊成一片。这里保证短边够长，由画的那一头裁。
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
