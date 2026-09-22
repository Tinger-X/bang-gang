
using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 一条消息的气泡。**不是控件** —— 位置由 <see cref="ChatView"/> 排，像素由
/// 它的三个绘制阶段画在 <see cref="ChatView"/> 的 Graphics 上。
///
/// 0.9.0 之前它是真实的子 HWND，那次改动的原因是侧栏动画期间的卡顿与破损：
/// 子窗口**不在父控件的双缓冲里**，父面板重画时先铺自己的底色、子窗口随后才被系统 blit
/// 上去 —— 中间那一瞬就是用户看到的破损；而每一帧给 N 个气泡 <c>SetBounds</c>
/// 就是卡顿。改成整块自绘之后，一次 <c>Invalidate</c>、一次双缓冲合成、一次原子 blit。
///
/// 0.9.3 起**不再烘位图缓存**，绘制拆成三遍（<see cref="DrawBack"/> /
/// <see cref="DrawText"/> / <see cref="DrawOver"/>），由 <see cref="ChatView"/>
/// 把三遍提到**所有**气泡的外层 —— 整屏文字共用一次 HDC 借还。
/// 文字直接落在对话区的不透明双缓冲上：ClearType 对着真实底色混色才是对的，
/// 画进透明位图再贴的老路会让它对着透明像素（读作黑）混 —— 亮色主题下深字边缘
/// 一圈黑晕，就是用户报的「重影」。三遍直画 + 逐行裁剪之后，一帧的成本只跟
/// **看得见的那几行**有关，缓存也就没有了存在必要。
/// GDI 文本不认 <c>Graphics.Transform</c>，所以文字那一遍只能用绝对坐标画。
///
/// 附件（图片 / 文件）画在气泡**外面、上方**，不占用气泡内部（用户要求）。
/// 所以整条消息的纵向其实是两段：附件区 + 气泡体，分界是 <see cref="_bubbleTop"/>。
/// </summary>
internal sealed class MessageBubble
{
    public ChatMessage Msg { get; }
    public bool IsUser { get; }

    /// <summary>点到了其中一张图片。参数是那张图的附件（路径留给放大浮层自己去读原图）。</summary>
    public event Action<Attachment>? ImagePressed;

    /// <summary>
    /// 气泡自己长高或变矮了（点开 / 收起「思考过程」）。对话区收到要**重排**。
    ///
    /// 必须报出去：气泡的尺寸是它自己算的，而位置是 <c>ChatView.PlaceRows</c> 排的，
    /// 下面那些气泡不会自己让位 —— 不收这一声，展开之后就会压着下一条消息。
    ///
    /// **注意 <see cref="SetMaxInner"/> 特意不报这个。** 它由 <c>ChatView</c> 自己调用，
    /// 而那之后紧接着就会重读 <see cref="Height"/>；在这里回声一次就成了
    /// <c>ReflowRows</c> → <see cref="Changed"/> → <c>ReflowRows</c> 的重入。
    /// </summary>
    public event Action? Changed;

    /// <summary>只有画面变了（悬浮高亮 / 流式收到新文字），**不用重排**。</summary>
    public event Action? Repaint;

    // ---------------- 几何 ----------------

    /// <summary>气泡在对话区坐标系里的位置。由 <see cref="ChatView.PlaceRows"/> 排。</summary>
    public Point Location;

    /// <summary>整条消息（附件区 + 气泡体）的尺寸。由 <see cref="Rebuild"/> 算。</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    public Rectangle Rect => new(Location, new Size(Width, Height));

    /// <summary>
    /// 气泡文本的最大内宽（不含左右内边距）。默认值是「对话区还没发话」时的兜底，
    /// 真实值由 <see cref="ChatView"/> 每轮布局推进来 —— 需求是气泡宽度随对话区自适应。
    /// </summary>
    private int _inner = 560;

    /// <summary>
    /// 内容**不受 <see cref="_inner"/> 约束**时想要的宽度。只有 <see cref="SetMaxInner"/>
    /// 的早退判据读它，见那里的注释。
    /// </summary>
    private float _naturalW;

    /// <summary>
    /// 上一轮排版时，内容**想要比 <see cref="_inner"/> 更宽**（正文折了行、附件排到了第二行、
    /// 思考块 / 截断说明强制满宽）。这种气泡的上限一变，宽度就该跟着变 ——
    /// <see cref="SetMaxInner"/> 靠它把自己和「内容撑出来的窄气泡」分开。
    /// 四条判据各自算得准不准，见 <see cref="Rebuild"/> 里那一段。
    /// </summary>
    private bool _capped;

    /// <summary>气泡左右内边距。对话区要按它反推「内宽上限」，所以不能是 private。</summary>
    internal const int PadX = 14;

    private const int PadY = 11;

    // ---- 思考过程那块（只有助手消息、且模型真的吐了 reasoning_content 时才有） ----
    private const int ReasonHeadH = 20;   // 「思考过程」那一行的行高，也是点击热区的高度
    private const int ReasonPadY = 9;     // 思考正文框的上下内边距
    private const int ReasonRuleW = 3;    // 正文左边那道竖线（连间距）
    private const int ReasonPadX = 10;

    // ---- 工具调用那块（助手消息调过工具时才有，见 ToolCalls） ----
    //
    // 和思考块是**同一个形状**（一行可折叠的表头 + 带竖线的旁白框），所以行高、内边距、
    // 竖线宽度全都复用上面的常量、连画法都走同一个 DrawFoldHeader / DrawFoldBody。
    // 各写一套的话，两个块的箭头大小和文字基线会慢慢分家，而那种差别没人说得清是不是 bug。
    private const int ToolRowGap = 9;      // 展开时，两条调用之间的间距

    /// <summary>展开时圆点占的宽度：文字要给它让位，否则会读成「•计算」连在一起。</summary>
    private const int ToolBulletW = 13;

    /// <summary>展开时每条调用最多显示多少个字符的结果。全文在 <c>ToolCall.Result</c> 里存着，
    /// 但这里是气泡，不是日志阅读器 —— 一条读了半个文件的记录展开后能顶满整个屏幕。</summary>
    private const int ToolPreviewMax = 240;

    // ---- 截断说明（回复被长度上限截断时贴在正文下面那句） ----
    private const int WarnPad = 9;
    private const int WarnGap = 10;       // 与上方正文之间的间距

    // ---- 附件区（图片 + 文件，和输入框上方那套是同一批卡片） ----
    private const int ChipGapY = 8;       // 换行时上一行的下缘与下一行的上缘
    private const int AttachGap = 8;      // 附件区与**气泡顶缘**之间（附件在气泡外面）

    /// <summary>
    /// 一个附件格子：来源附件 + 缩略图（只有图片、且真的读出来了才有）+ 它的矩形。
    ///
    /// 位置由 <see cref="LayoutChips"/> 一次算好，画、量高、命中测试都读它 ——
    /// 三处各算一遍的话，一旦算岔，表现是图片和正文重叠几个像素，谁也不会一眼看出来。
    /// </summary>
    private sealed class Chip
    {
        public required Attachment Src;

        /// <summary>消息里标着这是图片。**能不能点开看它**（不看缩略图读没读出来）——
        /// 缩略图读不出来时仍然画一个占位格子，用户至少知道这儿有张图。</summary>
        public required bool IsImage;

        public Image? Thumb;
        public Rectangle Rect;
    }

    private readonly List<Chip> _chips = new();

    private Markdown.Layout? _md;

    /// <summary>思考块占的总高（含它与下方内容的间距）；0 = 这条消息没有思考块。</summary>
    private float _reasonH;
    private float _reasonBodyH;
    private Rectangle _reasonHeader;

    /// <summary>思考正文展开着没有。用户自己点过之后就不再自动收放，见 <see cref="_reasonTouched"/>。</summary>
    private bool _reasonOpen;

    /// <summary>用户手动点过表头。点过之后流式那一套自动收放就靠边站 —— 他刚说想看着。</summary>
    private bool _reasonTouched;

    private bool _reasonHover;

    /// <summary>工具块占的总高（含它与下方内容的间距）；0 = 这条消息没调过工具。</summary>
    private float _toolH;
    private float _toolBodyH;
    private Rectangle _toolHeader;
    private bool _toolOpen;
    private bool _toolTouched;
    private bool _toolHover;

    /// <summary>
    /// 展开时每条调用的几何。**量的时候一次算好，画的时候只读** ——
    /// 和 <see cref="Chip"/> 一个道理：量和画各算一遍的话，一旦算岔，
    /// 表现是文字叠在一起或超出那个框，而两处看着都「没错」。
    /// </summary>
    private sealed class ToolRow
    {
        public string Brief = "";
        public string Preview = "";
        public float BriefH;
        public float PreviewH;
        public float Y;          // 相对正文框顶
    }

    private readonly List<ToolRow> _toolRows = new();

    /// <summary>截断说明那块的高度（含与上方正文的间距）；0 = 这条消息没有说明。</summary>
    private float _warnH;

    /// <summary>
    /// 气泡圆角矩形的上缘。有附件时就是附件区的高度（+ 间距），没有时是 0。
    ///
    /// **所有气泡内部的纵向坐标都要加它** —— <see cref="BodyTop"/>、
    /// <see cref="MeasureReason"/>、<see cref="Render"/> 里的「帮帮」那几处。
    /// 不统一的话表现是正文和气泡边框差几个像素，谁也不会一眼看出来。
    /// </summary>
    private int _bubbleTop;

    /// <summary>附件区最宽那一行有多宽。气泡的内容宽度要把它算进去。</summary>
    private int _chipW;

    /// <summary>附件是不是被 <see cref="_inner"/> 挤到了第二行。同上，早退判据要读它。</summary>
    private bool _chipWrapped;

    /// <summary>
    /// 气泡体这一块到底画不画。只有附件、没有正文的用户消息是 **false** —— 那时气泡里
    /// 一个字都不会有，画出来就是个空壳（用户明确要求去掉它，见 <see cref="Rebuild"/>）。
    /// </summary>
    private bool _bubbleBody;

    /// <summary>这条消息正等着模型吐第一个字（见 <see cref="SetWaiting"/>）。</summary>
    private bool _waiting;

    /// <summary>动画走到第几帧。由 <c>ChatView</c> 的计时器推 —— 气泡自己没有时钟。</summary>
    private int _phase;

    /// <summary>
    /// 这一帧到底画不画等待动画。**由 <see cref="Rebuild"/> 算、由 <see cref="Render"/> 读**，
    /// 不在两处各判一次 —— 判据一分叉，就会出现「高度按有动画算、画的时候没有」
    /// （气泡底下多一块空白）或者反过来（三个点压出气泡外）。
    ///
    /// 它比 <see cref="_waiting"/> 严：内容一到（哪怕是思考）就该收掉，
    /// 否则动画会和真正的正文抢同一行。
    /// </summary>
    private bool _showWait;

    /// <summary>指针停在上面那个图片格子的序号，-1 表示没停在任何一格上。</summary>
    private int _hover = -1;

    /// <summary>
    /// 收起来的代码块的序号（<see cref="Markdown.CodeHead.Ordinal"/>）。是 Measure 的
    /// 参数之一、也进它的缓存键 —— 「收起着」和「展开着」是两份不同的排版。
    /// </summary>
    private readonly HashSet<int> _codeFolded = new();

    /// <summary>指针悬停的代码块头部按钮：块序号 + 哪个钮（0=复制，1=收展）。-1 = 没悬停。</summary>
    private int _codeHotOrd = -1, _codeHotBtn = -1;

    /// <summary>刚复制成功的代码块序号：复制图标短暂换成勾，1.2 秒后还回去。</summary>
    private int _codeCopiedOrd = -1;

    public MessageBubble(ChatMessage msg, bool isUser)
    {
        Msg = msg;
        IsUser = isUser;
        // 从历史里读回来的消息一律**收起**：那是几天前的一轮思考，用户现在要读的是回答。
        // 流式那一轮的 assistant 消息正文此刻还是空的，于是从展开开始长（见 Rebuild）。
        _reasonOpen = (msg.Text ?? "").Length == 0;
        BuildChips();
        Rebuild();
    }

    /// <summary>
    /// 正文变化后重排一次（流式回复用）。
    ///
    /// 特意与附件分开：附件是从磁盘读图 + 缩放出来的，每收一小段就重读一次磁盘上的图，
    /// 一条回复下来能把同一个文件读上百遍。附件在一条消息的生命周期里不会变，读一次就够。
    ///
    /// 只报 <see cref="Repaint"/>：调用方（<c>MainForm.PaintStream</c>）紧接着就会调
    /// <c>ChatView.NotifyRowGrew</c>，重排由那一次负责。这里再报一次就是重排两遍。
    /// </summary>
    public void RefreshText()
    {
        Rebuild();
        Repaint?.Invoke();
    }

    /// <summary>
    /// 对话区给的可用内宽上限。侧栏展开 / 收起时**每一帧**都会调它（对话区宽度在变）。
    ///
    /// 早退那一条是这里唯一的技巧：气泡宽度 = min(内宽上限, 内容自然宽度) + 内边距，
    /// 所以当内容比**新旧两个上限里更小的那个**还窄时，说明宽度本来就是内容撑的、
    /// 没被上限卡住，换一个上限不会改变任何东西 —— 不必重排，也不必重画。
    /// 侧栏动画期间省掉的正是这一整批 <c>Markdown.Measure</c>。
    ///
    /// **<see cref="_capped"/> 那一条不能省。** 折行正文量出来的「自然宽度」是
    /// 「最长那条物理行有多宽」，贪心折行总在放下下一个词之前就换行，于是它比上限窄着
    /// 那一个词的宽度 —— 超长正文量出来 803、上限 849，只比 <c>_naturalW</c> 的话，
    /// 上限变大时 <c>_naturalW &lt;= Math.Min(old, inner)</c> 恒成立：一条超长消息在侧栏收起、
    /// 可用宽度多出 230px 之后会原地不动 —— 气泡永远停在它第一次排版时的宽度上，
    /// 而这件事在几何上完全看不出是「没跟上」还是「就该这么宽」。
    /// 所以判据里必须带上「内容到底想不想要更宽」，由排版那边当场记下来。
    /// </summary>
    public void SetMaxInner(int inner)
    {
        if (inner == _inner) return;
        int old = _inner;
        _inner = inner;
        if (!_capped && _naturalW <= Math.Min(old, inner)) return;
        Rebuild();
        Repaint?.Invoke();
    }

    /// <summary>这条消息正等着模型吐第一个字。</summary>
    public bool Waiting => _waiting;

    /// <summary>
    /// 发起请求 / 收到第一个字符时由 <see cref="ChatView"/> 置。
    ///
    /// 返回**变没变**：变了就得重排 —— 气泡的高度跟着它涨一截（见 <see cref="Rebuild"/>），
    /// 而高度是这个类自己算的，不报出去下面的气泡不会让位。
    /// </summary>
    public bool SetWaiting(bool on)
    {
        if (_waiting == on) return false;
        _waiting = on;
        _phase = 0;
        Rebuild();
        Repaint?.Invoke();
        return true;
    }

    /// <summary>
    /// 动画往前走一帧。由 <c>ChatView</c> 的计时器推 —— 气泡自己没有时钟。
    /// </summary>
    public void TickWait()
    {
        if (!_showWait) return;
        _phase++;
        Repaint?.Invoke();
    }

    /// <summary>依据消息重建内部布局并计算气泡尺寸（附件区在上、气泡体在下）。</summary>
    private void Rebuild()
    {
        // 正文取一次存成局部变量，后面都读它。
        //
        // 两个理由。一是**确实可能是 null**：消息是从磁盘上的 JSON 反序列化回来的，
        // System.Text.Json 会把 `"Text": null` 直接塞进这个声明为不可空的属性里，
        // 声明的非空拦不住它（上面 Attachments 那一手也是同一个原因）。
        // 二是编译器认这个理：`Msg.Text ?? ""` 会让它把 `Msg.Text` 的流状态记成
        // 「可能为空」，之后再解引用同一个属性就报 CS8602 —— 同一件事在方法里
        // 说两遍，两遍的说法还不一样。存成局部变量，「可能为空」只在取名那一行出现。
        string text = Msg.Text ?? "";
        string reason = Msg.Reasoning ?? "";
        string warn = Msg.Warning ?? "";

        // 一轮回复里，「思考」在前、「正文」在后。正文一开始冒出来就把它收起来：
        // 不然一屏思考把回答顶到看不见的地方，用户还得先滚过去。
        // 用户自己点开过就不再动它 —— 那一刻起这块归他管。
        if (!IsUser && reason.Length > 0 && !_reasonTouched) _reasonOpen = text.Length == 0;

        // 工具块同一条规矩：正文一出来就把「调了哪几个工具」收成一行。
        // 它比思考块更该收 —— 工具往返常常是好几条，展开着一屏就没了。
        if (!IsUser && !_toolTouched) _toolOpen = text.Length == 0 && reason.Length == 0;

        // 只有附件、没有正文的用户消息：**不画那个空气泡**，只把附件列出来（用户要求）。
        //
        // 判据是「气泡体里一个字都不会有」，所以助手消息恒为真 —— 它顶部那行「帮帮」就画在
        // 这块里，连头一起去掉的话，一条只有思考块的消息会连名字都不剩。
        //
        // `_chips.Count == 0` 那一条也不能省：没有任何内容的消息还是落回下面那个 28px 的矮
        // 气泡。高度算成 0 的话它在列表里凭空消失，用户只会看到「这儿少了一条」。
        _bubbleBody = !IsUser || text.Length > 0 || warn.Length > 0 || _chips.Count == 0;

        // 附件**先排**，因为它决定气泡顶缘在哪儿；这之后所有纵向坐标都带 _bubbleTop。
        //
        // 没有气泡体时附件直接贴着整条消息的上缘排，左右也不再留气泡内边距 ——
        // 那 2×PadX 本来是给气泡当内衬的，气泡没了，它就只是一块让图飘在离右缘 30px 处
        // 的空白（气泡行是右对齐的，多出来的宽度全落在图的右边）。
        int chipsH = LayoutChips(_bubbleBody ? PadX : 0);
        _bubbleTop = chipsH == 0 ? 0 : chipsH + (_bubbleBody ? AttachGap : 0);

        _md = Markdown.Measure(text, _inner, _codeFolded.Count > 0 ? _codeFolded : null);
        MeasureReason(reason);
        // **必须在 MeasureReason 之后**：工具块的表头位置要叠上 _reasonH（见 MeasureTool）。
        MeasureTool();
        _warnH = warn.Length > 0 ? WarnBoxH(warn) + WarnGap : 0;

        // 等第一个字的这段：正文一个字都还没有，气泡按真实内容量只有顶上那行「帮帮」
        // （40px 高、88px 宽），看上去像半截断掉的气泡。所以正文那一行按一行算，
        // 动画就画在那一行上（见 DrawWait）—— 第一个字符一到，_showWait 变假，
        // 高度正好交给真正的正文。
        _showWait = _waiting && _md.Height <= 0 && _reasonH <= 0 && _toolH <= 0;
        float bodyH = _showWait ? Markdown.BodyLinePitch() : _md.Height;

        float headH = IsUser ? 0 : 18;  // 助手消息顶部显示"帮帮"

        float natural = Math.Max(60f, _md.Width);
        if (_chipW > natural) natural = _chipW;
        // 思考块、截断说明、还有「等第一个字」那三个点，固定占满整个内宽：它们都是
        // 「模型在干什么」的一条旁白，而不是模型说了什么。按内容量宽的结果是「帮帮」
        // 加三个点只撑出 88px 的一小块，右边空着一大片，看着像个没长开的空壳；
        // 而跟着正文一起忽宽忽窄，流式的时候整条气泡会一直跳。
        // 写成「想要无限宽」而不是「想要 _inner 宽」：早退判据读的就是这个数，
        // 写成后者会让「上限变小了但还够用」被误判成不必重排。
        //
        // 代价是第一个字到达时宽度会收一次（满宽 → 正文宽）。这是这条规矩本来的样子，
        // 思考块早就在这么做，不是这里新开的口子。
        if (_reasonH > 0 || _toolH > 0 || _warnH > 0 || _showWait) natural = float.MaxValue;
        _naturalW = natural;

        // 这一轮的内容有没有把上限顶满 —— 见 SetMaxInner。
        //
        // 正文那一条**不能**拿宽度去比：折行之后 Markdown 报出来的宽度是「最长那条物理行
        // 有多宽」，而贪心折行总是在放下下一个词**之前**就换行，于是它比上限窄着那一个词的
        // 宽度 —— 15 句话的段落量出来 803、上限 849，差 46px，读成「内容就想要 803」
        // 正好会把「侧栏收起后还停在旧宽度」这件事放过去（0.8.5 就是这么栽的第二次）。
        // 所以 Markdown 折行的那一刻自己记下 Wrapped，这里只读那个标记。
        _capped = _md.Wrapped || _chipWrapped || _reasonH > 0 || _toolH > 0 || _warnH > 0;

        float contentW = Math.Min(_naturalW, _inner);
        Width = (int)Math.Min(_inner + PadX * 2, contentW + PadX * 2);
        Height = (int)(_bubbleTop + PadY + headH + _reasonH + _toolH + bodyH + _warnH + PadY);
        if (!_bubbleBody)
        {
            // 宽度就是附件本身，高度就是附件区 —— 见上面那两处注释。
            Width = (int)Math.Max(1f, Math.Min(_chipW, _inner));
            Height = _bubbleTop;
        }
        // 什么都还没有的 28px 矮气泡。**等第一个字的时候不算** —— 那会儿正文也一个字都没有，
        // 正好落进这一句，把上面刚预留出来的正文行连同动画一起压掉，气泡又变回用户报的那半截。
        if (text.Length == 0 && reason.Length == 0 && _chips.Count == 0 && _toolH <= 0 && !_showWait) Height = 28;

        // 重排后悬浮下标可能指到了另一格上（甚至指到了不存在的下标）
        if (_hover >= _chips.Count) _hover = -1;
    }

    /// <summary>
    /// 气泡**内部**内容区的起点：跳过附件区、气泡内边距、助手名字、思考块和工具块。
    /// 绘制、量尺寸、命中测试都走它，别各自把「思考块有多高」再算一遍 ——
    /// 三处各算一遍的那种错法，表现是正文和附件重叠一点点，谁也不会一眼看出来。
    /// </summary>
    private float BodyTop() => _bubbleTop + PadY + (IsUser ? 0 : 18) + _reasonH + _toolH;

    /// <summary>思考正文的可用宽度。</summary>
    private int ReasonTextW() => _inner - ReasonPadX * 2 - ReasonRuleW;

    /// <summary>量一次思考块（收起时只量表头那一行，正文一个字都不排）。</summary>
    private void MeasureReason(string reason)
    {
        _reasonH = 0;
        _reasonBodyH = 0;
        _reasonHeader = Rectangle.Empty;
        if (IsUser || reason.Length == 0) return;

        float top = _bubbleTop + PadY + 18;    // 表头永远紧跟在「帮帮」下面
        _reasonHeader = new Rectangle(PadX, (int)Math.Round(top), _inner, ReasonHeadH);
        _reasonH = ReasonHeadH + 10;           // 收起：只有表头 + 与下方内容的间距
        if (!_reasonOpen) return;

        using var f = Theme.UI(10.5f);
        var sz = TextRenderer.MeasureText(reason, f, new Size(ReasonTextW(), int.MaxValue),
                                          TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        _reasonBodyH = sz.Height + ReasonPadY * 2;
        _reasonH = ReasonHeadH + 6 + _reasonBodyH + 10;
    }

    /// <summary>
    /// 量一次工具块（收起时只量表头那一行，每条调用的文字一个都不排）。
    ///
    /// 表头的 y **必须叠上 <c>_reasonH</c>**：思考块永远在工具块上面，而
    /// <see cref="MeasureReason"/> 的表头写的是 <c>_bubbleTop + PadY + 18</c>、
    /// 它不知道自己下面还有东西。这也是 <c>Rebuild</c> 里两个 Measure 的调用顺序不能反的原因
    /// （反了就拿到上一次的 _reasonH，展开/收起一次思考就会错位）。
    /// </summary>
    private void MeasureTool()
    {
        _toolH = 0;
        _toolBodyH = 0;
        _toolHeader = Rectangle.Empty;
        _toolRows.Clear();

        var calls = Msg.ToolCalls;
        if (IsUser || calls == null || calls.Count == 0) return;

        float top = _bubbleTop + PadY + 18 + _reasonH;
        _toolHeader = new Rectangle(PadX, (int)Math.Round(top), _inner, ReasonHeadH);
        _toolH = ReasonHeadH + 10;             // 收起：只有表头 + 与下方内容的间距
        if (!_toolOpen) return;

        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        int w = ReasonTextW() - ToolBulletW;   // 圆点占掉的那一条，文字不能压上去
        using var fb = Theme.UI(10.5f);
        using var fp = Theme.UI(10f);

        float y = 0;
        foreach (var c in calls)
        {
            var row = new ToolRow
            {
                Brief = RowBrief(c),
                Preview = RowPreview(c),
            };
            row.BriefH = TextRenderer.MeasureText(row.Brief, fb, new Size(w, int.MaxValue), flags).Height;
            row.PreviewH = row.Preview.Length > 0
                ? TextRenderer.MeasureText(row.Preview, fp, new Size(w, int.MaxValue), flags).Height
                : 0;
            row.Y = y;
            y += row.BriefH + (row.PreviewH > 0 ? 3 + row.PreviewH : 0) + ToolRowGap;
            _toolRows.Add(row);
        }
        if (_toolRows.Count > 0) y -= ToolRowGap;      // 最后一条后面不留间距

        _toolBodyH = y + ReasonPadY * 2;
        _toolH = ReasonHeadH + 6 + _toolBodyH + 10;
    }

    /// <summary>展开时每条调用的一行小标题：「· 计算 (1234*5678)/2 · 0.0s」，失败的标出来。</summary>
    private static string RowBrief(ToolCall c)
    {
        string brief = string.IsNullOrWhiteSpace(c.Brief) ? c.Name : c.Brief;
        string s = "• " + brief;
        if (!c.Ok) s = "• " + brief + "（失败）";
        if (c.Ms > 0) s += " · " + Secs(c.Ms);
        return s;
    }

    /// <summary>展开时每条调用的结果预览。全文太长（读文件动辄上千字），这里只给个开头。</summary>
    private static string RowPreview(ToolCall c)
    {
        string r = (c.Result ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (r.Length == 0) return "";
        // 缩进两格并原样保留换行：结果里常常是多行（表格、文件片段），
        // 压成一行的话用户完全认不出那是什么。
        string body = "  " + r.Replace("\n", "\n  ");
        return body.Length > ToolPreviewMax ? body[..ToolPreviewMax] + "…" : body;
    }

    /// <summary>表头那一行显示什么。还没开始回答时是「正在使用工具…」，答完才是次数与总耗时。</summary>
    private string ToolLabel()
    {
        // 直接判 calls 而不是先取个 n：编译器认不出「n == 0 已经挡掉了 null」，
        // 拆成两步反倒要给它加一个 ! 才编得过。
        var calls = Msg.ToolCalls;
        if (calls == null || calls.Count == 0) return "工具调用";
        int n = calls.Count;

        bool done = (Msg.Text ?? "").Length > 0;
        int ms = 0;
        foreach (var c in calls) ms += c.Ms;

        if (!done) return n == 1 ? "正在使用工具…" : "正在使用工具（" + n + " 个）…";
        return "工具调用 · " + n + " 次" + (ms > 0 ? " · " + Secs(ms) : "");
    }

    /// <summary>截断说明那块盒子有多高（文字按内宽折行量出来）。</summary>
    private float WarnBoxH(string warn)    {
        using var f = Theme.UI(10.5f);
        var sz = TextRenderer.MeasureText(warn, f, new Size(_inner - WarnPad * 2, int.MaxValue),
                                          TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return sz.Height + WarnPad * 2;
    }

    /// <summary>
    /// 排附件格子，返回**格子本身**占的高度（不含它与下方气泡的间距，那个由
    /// <see cref="Rebuild"/> 按这只气泡有没有气泡体决定加不加）。
    ///
    /// 从 y = 0 起排 —— 附件在气泡**外面**（用户要求），所以这一块的坐标原点就是整条消息的
    /// 上缘，而不是气泡内边距之下。气泡的圆角矩形整体下移到它下面（见 <see cref="_bubbleTop"/>）。
    ///
    /// <paramref name="padX"/> 是左起的留白：有气泡体时给 <see cref="PadX"/>（接上气泡的内衬），
    /// 没有时给 0 —— 那时整条消息就只剩这些格子，再留一圈就是纯粹的空白，见 <see cref="Rebuild"/>。
    ///
    /// 卡片本身就是输入框上方那一套 —— 图片是 44×44 的方图块，文件是「类别色块 + 名字 + 大小」
    /// 的小横条，几何常量只在 <see cref="DraftStrip"/> 一处定义，这里照着用。
    ///
    /// 与输入框那边**故意不同**的一点：那边一行放不下就先压窄、再放不下给「+N」；
    /// 这里换行。输入卡片的高度是定死的（挤掉一行就是挤掉用户正在敲的字），而气泡可以往上长，
    /// 用户自己加进来的附件必须全都看得见。
    /// </summary>
    private int LayoutChips(int padX)
    {
        _chipWrapped = false;
        if (_chips.Count == 0) { _chipW = 0; return 0; }

        int x = padX, y = 0, widest = 0, right = padX + _inner;
        foreach (var c in _chips)
        {
            int w = c.IsImage ? DraftStrip.ChipImgW : Math.Min(DraftStrip.ChipNatW, _inner);
            if (x > padX && x + w > right) { x = padX; y += DraftStrip.ChipH + ChipGapY; _chipWrapped = true; }
            c.Rect = new Rectangle(x, y, w, DraftStrip.ChipH);
            x += w + DraftStrip.ChipGap;
            if (x - DraftStrip.ChipGap > widest) widest = x - DraftStrip.ChipGap;   // 这一行的右缘
        }
        _chipW = widest - padX;
        return y + DraftStrip.ChipH;
    }

    /// <summary>
    /// 把消息里的附件读成格子。只在构造时跑一次 —— 缩略图是从磁盘读图再缩放出来的，
    /// 而流式回复每收一小段就要 <see cref="RefreshText"/> 一次，跟着重读的话
    /// 一条回复下来能把同一个文件读上百遍。
    /// </summary>
    private void BuildChips()
    {
        _chips.Clear();
        if (Msg.Attachments == null) return;
        foreach (var a in Msg.Attachments)
        {
            bool isImg = a.Kind == "image";
            _chips.Add(new Chip
            {
                Src = a,
                IsImage = isImg,
                // 缩略图走 DraftStrip 那一套：cover 裁切 + **抗锯齿**圆角。
                // 原来是自己读一张 280×190 的大图再磨角，格子缩到 44px 之后
                // 那份开销（和解码内存）纯属白给。
                Thumb = isImg ? DraftStrip.MakeThumb(a) : null,
            });
        }
    }

    /// <summary>
    /// 松手：气泡不再是控件，没有 <c>Dispose(Control)</c> 那一套，
    /// 但缩略图是 GDI+ 对象，得自己还回去。由 <see cref="ChatView"/> 在清空行时调。
    /// </summary>
    public void Dispose()
    {
        foreach (var c in _chips) c.Thumb?.Dispose();
        _chips.Clear();
    }

    // ---------------- 命中测试 ----------------

    /// <summary>命中的图片格序号（没有则 -1）。**文件格子点不动**（用户明确要求），
    /// 所以它不参与命中测试 —— 不这么写的话它会先「悬浮变一下」再什么都不发生。</summary>
    private int ChipAt(Point p)
    {
        for (int i = 0; i < _chips.Count; i++)
            if (_chips[i].IsImage && _chips[i].Rect.Contains(p)) return i;
        return -1;
    }

    private bool ReasonHeadAt(Point p) => _reasonH > 0 && _reasonHeader.Contains(p);

    private bool ToolHeadAt(Point p) => _toolH > 0 && _toolHeader.Contains(p);

    /// <summary>点表头 = 展开 / 收起那一块。整行都是热区，箭头本身太小不好点。</summary>
    private void ToggleReason()
    {
        _reasonOpen = !_reasonOpen;
        _reasonTouched = true;    // 从此不再跟着「正文开始了没有」自动收放
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>同上，工具块那张表头。</summary>
    private void ToggleTool()
    {
        _toolOpen = !_toolOpen;
        _toolTouched = true;
        Rebuild();
        Changed?.Invoke();        // 高度变了，必须让 ChatView 重排
    }

    /// <summary>指针移到本气泡上（坐标已由 <see cref="ChatView"/> 换算成局部坐标）。</summary>
    public void MouseMove(Point p)
    {
        // 两个可折叠的表头：指针形状不改（用户要求），所以「这一行能点」只能靠
        // 悬浮时文字变个颜色来暗示。两块各记各的悬浮态，否则一块变色的同时另一块也亮。
        bool overReason = ReasonHeadAt(p);
        bool overTool = ToolHeadAt(p);
        if (overReason != _reasonHover || overTool != _toolHover)
        {
            _reasonHover = overReason;
            _toolHover = overTool;
            Repaint?.Invoke();
        }

        // 代码块头部按钮：同上，悬浮变色是唯一的「能点」暗示
        var cb = CodeBtnAt(p);
        if (cb.Ord != _codeHotOrd || cb.Btn != _codeHotBtn)
        {
            _codeHotOrd = cb.Ord;
            _codeHotBtn = cb.Btn;
            Repaint?.Invoke();
        }

        // 指针形状不改（用户要求），所以「这里能点」只能靠悬浮时把图压暗一点点来暗示
        int i = ChipAt(p);
        if (i == _hover) return;
        _hover = i;
        Repaint?.Invoke();
    }

    /// <summary>指针离开本气泡。</summary>
    public void MouseLeave()
    {
        bool dirty = _hover >= 0 || _reasonHover || _toolHover || _codeHotOrd >= 0;
        _hover = -1;
        _reasonHover = false;
        _toolHover = false;
        _codeHotOrd = -1;
        _codeHotBtn = -1;
        if (dirty) Repaint?.Invoke();
    }

    /// <summary>在本气泡上松开左键。</summary>
    public void MouseUp(Point p)
    {
        if (ReasonHeadAt(p)) { ToggleReason(); return; }
        if (ToolHeadAt(p)) { ToggleTool(); return; }

        var cb = CodeBtnAt(p);
        if (cb.Ord >= 0)
        {
            if (cb.Btn == 1) ToggleCode(cb.Ord);
            else CopyCode(cb.Ord);
            return;
        }

        int i = ChipAt(p);
        if (i >= 0) { ImagePressed?.Invoke(_chips[i].Src); return; }

        // 链接：排版时挂到 Run 上的地址（Markdown 的 ScanInline），打开走 Ui.OpenLink
        // 那条白名单（只 http/https）。打不开不说什么 —— 状态条是输入区那条通道，够不着这里。
        string? link = LinkAt(p);
        if (link != null) Ui.OpenLink(link);
    }

    /// <summary>点收展钮：翻转这块代码的折叠态。高度变了，得让 ChatView 重排。</summary>
    private void ToggleCode(int ord)
    {
        if (!_codeFolded.Remove(ord)) _codeFolded.Add(ord);
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>
    /// 点复制钮：把这块代码的**原文**（不是着色后的那一串 Run）写进剪贴板。
    /// 成功了图标短暂换成一个勾（1.2s），用 UI 线程的同步上下文定时还回去 ——
    /// 气泡自己不养时钟（和「等待动画由 ChatView 推」是同一条规矩）。
    /// </summary>
    private void CopyCode(int ord)
    {
        if (_md == null) return;
        foreach (var h in _md.CodeHeads)
        {
            if (h.Ordinal != ord) continue;
            try { Clipboard.SetText(h.Code); Trace.Log($"code block copied ord={ord} len={h.Code.Length}"); }
            catch (Exception ex) { Trace.Log("code block copy failed: " + ex.Message); }
            _codeCopiedOrd = ord;
            Repaint?.Invoke();
            var ts = TaskScheduler.FromCurrentSynchronizationContext();
            _ = Task.Delay(1200).ContinueWith(_ =>
            {
                if (_codeCopiedOrd != ord) return;
                _codeCopiedOrd = -1;
                Repaint?.Invoke();
            }, ts);
            return;
        }
    }

    // ---- 代码块头部按钮的几何 ----

    private const float CodeBtnBox = 22f;    // 命中框（图标本身只有 13px，按的地方要大一圈）
    private const float CodeBtnGap = 2f;
    private const float CodeBtnIcon = 13f;

    /// <summary>头部矩形从 markdown 局部坐标换算到气泡局部坐标。</summary>
    private RectangleF CodeHeadLocal(Markdown.CodeHead h) =>
        new(PadX + h.Rect.X, BodyTop() + h.Rect.Y, h.Rect.Width, h.Rect.Height);

    private static RectangleF FoldBtnRect(RectangleF head) =>
        new(head.Right - 8 - CodeBtnBox, head.Y + (head.Height - CodeBtnBox) / 2f, CodeBtnBox, CodeBtnBox);

    private static RectangleF CopyBtnRect(RectangleF head)
    {
        var f = FoldBtnRect(head);
        return new RectangleF(f.X - CodeBtnGap - CodeBtnBox, f.Y, CodeBtnBox, CodeBtnBox);
    }

    /// <summary>点中的代码块头部按钮（块序号 + 0=复制 / 1=收展），没点中是 (-1, -1)。</summary>
    private (int Ord, int Btn) CodeBtnAt(Point p)
    {
        if (_md == null || _showWait || !_bubbleBody) return (-1, -1);
        foreach (var h in _md.CodeHeads)
        {
            var hr = CodeHeadLocal(h);
            if (p.Y < hr.Top || p.Y >= hr.Bottom) continue;
            if (FoldBtnRect(hr).Contains(p)) return (h.Ordinal, 1);
            if (CopyBtnRect(hr).Contains(p)) return (h.Ordinal, 0);
            return (-1, -1);    // 落在这一行头部里了：不是按钮就是没点中，别再扫后面的块
        }
        return (-1, -1);
    }

    /// <summary>
    /// 点中的那段链接的地址，没点中是 null。
    ///
    /// 位置是**当场**按排版结果重算的（与 Markdown.Draw 的累加逐行对应），不落一张命中表
    /// —— 排版结果自己就是那张表，再抄一份出来早晚会和它岔开（流式期间它 40ms 就换一版）。
    /// </summary>
    private string? LinkAt(Point p)
    {
        if (_md == null || _showWait || !_bubbleBody) return null;
        float y = BodyTop();
        foreach (var pl in _md.Lines)
        {
            float top = y + pl.SpaceBefore;
            y = top + pl.Pitch;
            if (p.Y < top || p.Y >= y) continue;

            float runX = PadX + pl.Indent;
            foreach (var r in pl.Runs)
            {
                float at = r.X >= 0 ? PadX + r.X : runX;
                if (r.Link != null && p.X >= at && p.X < at + r.Advance) return r.Link;
                if (r.X < 0) runX += r.Advance;
            }
            return null;    // 已经落在这一行里：这一行没有链接就是没点中，别再扫后面的行
        }
        return null;
    }

    // ---------------- 绘制（三遍，由 ChatView 提到所有气泡的外层） ----------------

    /// <summary>正文底部（截断说明接在它下面）。等首个字符时是预留的那一行。</summary>
    private float BodyBottom() => BodyTop() + (_showWait ? Markdown.BodyLinePitch() : (_md?.Height ?? 0));

    /// <summary>
    /// 第一遍：所有铺在文字**下面**的 GDI+ 东西（气泡底 / 描边、附件缩略图、思考块的
    /// 箭头与底板、等待的三个点、截断说明的底板、以及正文的装饰与公式线）。
    ///
    /// 这一遍允许平移变换（GDI+ 认 <c>Graphics.Transform</c>），所以内部全用
    /// 气泡局部坐标画；<paramref name="visTop"/> / <paramref name="visBottom"/>
    /// 是 ChatView 坐标系里的可见带，进来先换算成局部的。
    /// </summary>
    public void DrawBack(Graphics g, Point at, float visTop, float visBottom)
    {
        if (Width <= 0 || Height <= 0) return;
        Color bg = IsUser ? Theme.UserBubble : Theme.AsstBubble;
        float vt = visTop - at.Y, vb = visBottom - at.Y;   // 可见带 → 本气泡的局部坐标

        var state = g.Save();
        g.TranslateTransform(at.X, at.Y);

        // 附件先画：它们在气泡**上面**（外面），所以气泡底不能盖住它们。
        for (int i = 0; i < _chips.Count; i++)
        {
            var c = _chips[i];
            if (c.Rect.Bottom < vt || c.Rect.Top > vb) continue;
            DrawChipBack(g, bg, c, i == _hover);
        }

        if (_bubbleBody)
        {
            using (var path = RoundedRect(0, _bubbleTop, Width - 1, Height - _bubbleTop - 1, 12))
            using (var b = new SolidBrush(bg))
                g.FillPath(b, path);
            if (!IsUser)
            {
                using var borderPen = new Pen(Color.FromArgb(150, 226, 230, 236), 1f);
                using var path2 = RoundedRect(0, _bubbleTop, Width - 1, Height - _bubbleTop - 1, 12);
                g.DrawPath(borderPen, path2);
            }

            if (!IsUser)
            {
                using (var hf = Theme.UI(10f, FontStyle.Bold))
                using (var hb = new SolidBrush(Theme.Accent))
                    g.DrawString("帮帮", hf, hb, PadX + 2, _bubbleTop + PadY);
                DrawReasonBack(g, bg, vt, vb);
                DrawToolBack(g, bg, vt, vb);
            }

            float y = BodyTop();
            if (_showWait) DrawWait(g, y, bg);
            else if (_md != null)
            {
                Markdown.DrawBack(g, _md, PadX, y, bg, vt, vb);
                DrawCodeHeads(g, bg, vt, vb);
            }
            if (_warnH > 0) DrawWarningBack(g, bg, vt, vb);
        }

        g.Restore(state);
    }

    /// <summary>
    /// 代码块头部的两个按钮（复制 / 收展），第一遍画（GDI+、气泡局部坐标）。
    /// 头部行本身（语言标签那行字）是排版出来的；按钮是**界面**，不归排版管 ——
    /// 排版只登记了头部矩形（<see cref="Markdown.Layout.CodeHeads"/>），图标在这里画。
    /// </summary>
    private void DrawCodeHeads(Graphics g, Color bubble, float vt, float vb)
    {
        if (_md == null || _md.CodeHeads.Count == 0) return;
        // 复制图标里「前面那张纸」要拿面板底色填掉（见 Gfx 的 Copy），
        // 底色必须和面板同出一个出处。
        Color panel = Markdown.InkColor(Markdown.Ink.CodePanel, bubble);

        foreach (var h in _md.CodeHeads)
        {
            var hr = CodeHeadLocal(h);
            if (hr.Bottom < vt || hr.Top > vb) continue;

            var copyBox = CenterIcon(CopyBtnRect(hr));
            Color ci = _codeHotOrd == h.Ordinal && _codeHotBtn == 0 ? Theme.Accent : Theme.TextMuted;
            if (_codeCopiedOrd == h.Ordinal) Gfx.DrawGlyph(g, Glyph.Check, copyBox, Theme.Accent);
            else Gfx.DrawGlyph(g, Glyph.Copy, copyBox, ci, 1.3f, panel);

            var foldBox = CenterIcon(FoldBtnRect(hr));
            Color fi = _codeHotOrd == h.Ordinal && _codeHotBtn == 1 ? Theme.Accent : Theme.TextMuted;
            Gfx.DrawGlyph(g, h.Folded ? Glyph.ChevRight : Glyph.ChevDown, foldBox, fi, 1.6f);
        }
    }

    /// <summary>把 22px 的命中框收成居中的 13px 图标框。</summary>
    private static RectangleF CenterIcon(RectangleF hit) =>
        new(hit.X + (hit.Width - CodeBtnIcon) / 2f, hit.Y + (hit.Height - CodeBtnIcon) / 2f,
            CodeBtnIcon, CodeBtnIcon);

    /// <summary>
    /// 第二遍：所有文字，走 ChatView 攥住的那一个 HDC（GDI 文本不认平移变换，
    /// 所以这一遍全部用 <paramref name="at"/> 换算出的**绝对**坐标）。
    /// </summary>
    public void DrawText(IDeviceContext dc, Point at, float visTop, float visBottom)
    {
        if (Width <= 0 || Height <= 0) return;
        Color bg = IsUser ? Theme.UserBubble : Theme.AsstBubble;
        float vt = visTop - at.Y, vb = visBottom - at.Y;

        for (int i = 0; i < _chips.Count; i++)
        {
            var c = _chips[i];
            if (c.Rect.Bottom < vt || c.Rect.Top > vb) continue;
            DrawChipText(dc, c, at);
        }

        if (!_bubbleBody) return;
        if (!IsUser) DrawReasonText(dc, at, vt, vb);
        if (!IsUser) DrawToolText(dc, at, vt, vb);
        if (!_showWait && _md != null)
            Markdown.DrawText(dc, _md, at.X + PadX, at.Y + BodyTop(), bg, visTop, visBottom);
        if (_warnH > 0) DrawWarningText(dc, at, vt, vb);
    }

    /// <summary>第三遍：正文里的下划线 / 删除线（压在文字上面）。</summary>
    public void DrawOver(Graphics g, Point at, float visTop, float visBottom)
    {
        if (Width <= 0 || Height <= 0 || !_bubbleBody || _showWait || _md == null) return;
        Color bg = IsUser ? Theme.UserBubble : Theme.AsstBubble;
        var state = g.Save();
        g.TranslateTransform(at.X, at.Y);
        Markdown.DrawOver(g, _md, PadX, BodyTop(), bg, visTop - at.Y, visBottom - at.Y);
        g.Restore(state);
    }

    /// <summary>
    /// 画「正在响应」的三个点。
    ///
    /// 让「正在动」这件事**只靠亮度**：三个点各自按相位在气泡底色和强调色之间混，
    /// 位置一个像素都不挪。靠位移的话每一帧整条气泡的观感都在抖，
    /// 而这个程序里因为「动画里跟着动的东西」栽过的次数已经不止一次（见 CLAUDE.md）。
    ///
    /// 点画在正文这一行的**左缘**（用户要求，0.9.4）：等待中的气泡是满宽的（见
    /// <see cref="Rebuild"/> 里 _showWait 那条），居中会让点悬在一大片空白中间，
    /// 而用户的眼睛那时正盯着左上角 —— 答案的第一个字也将落在那里。
    /// </summary>
    private void DrawWait(Graphics g, float top, Color bubble)
    {
        float pitch = Markdown.BodyLinePitch();
        const int n = 3;
        const float r = 3.5f, gap = 8f;
        float left = PadX + r;
        float cy = top + pitch / 2f;

        for (int i = 0; i < n; i++)
        {
            // 相位差走 1 格，三点依次点亮；取模两次是为了让 _phase 走满一圈回到 0
            // 之后仍然落在 0..2（C# 的 % 对负数给负值）。
            int step = (((_phase - i) % n) + n) % n;
            float k = step switch { 0 => 1.00f, 1 => 0.52f, _ => 0.26f };
            using var br = new SolidBrush(Theme.Mix(bubble, Theme.Accent, k));
            g.FillEllipse(br, left + i * (r * 2 + gap), cy - r, r * 2, r * 2);
        }
    }

    /// <summary>
    /// 附件格子的**图形**部分（第一遍）。图片就是那张缩略图本身（44×44 的方图块，
    /// 不写文件名 —— 缩略图比名字好认，「pic.png」和「pic-2.png」看不出区别，
    /// 两张缩略图一眼就分得开）；别的文件是「底色 + 类别图标」，
    /// 名字与大小在 <see cref="DrawChipText"/>（第二遍）。
    ///
    /// 缩略图读不出来的图片格画成文件样式（用扩展名当图标）：**不能悄悄跳过它** ——
    /// 用户明明加了张图，气泡里却什么都没有，那比画一个「打不开」的格子费解得多。
    /// </summary>
    private void DrawChipBack(Graphics g, Color bubble, Chip c, bool hover)
    {
        if (c.Thumb != null)
        {
            // 1:1 贴上去。PixelOffsetMode 必须校正：不校正的话整块图会被采样到半个像素上，
            // 四角的抗锯齿连同一整张图一起糊掉，看着就像「磨圆了但很脏」。
            var off = g.PixelOffsetMode;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(c.Thumb, c.Rect);
            g.PixelOffsetMode = off;

            // 悬浮提示：图变暗一点点。不用指针形状（用户要求），也不能什么都不给 ——
            // 「这张图能点开」在界面上否则无处可寻。
            if (hover)
            {
                using var path = RoundedRect(c.Rect.X, c.Rect.Y, c.Rect.Width - 1, c.Rect.Height - 1, DraftStrip.ThumbRadius);
                using var hl = new SolidBrush(Color.FromArgb(38, 0, 0, 0));
                g.FillPath(hl, path);
            }
            return;
        }

        RP.Box(g, c.Rect, 8, ChipFill(bubble), bubble);
        var icon = new Rectangle(c.Rect.Left + DraftStrip.IconPad,
                                 c.Rect.Top + (DraftStrip.ChipH - DraftStrip.IconSize) / 2,
                                 DraftStrip.IconSize, DraftStrip.IconSize);
        DraftStrip.PaintFileIcon(g, c.Src, icon, bubble);
    }

    /// <summary>附件格子的**文字**部分（第二遍）。坐标是绝对坐标（GDI 不认平移变换）。
    /// 宽度按卡片算出来，量多少画多少（见 DraftStrip.Ellipsize 的注释）。</summary>
    private static void DrawChipText(IDeviceContext dc, Chip c, Point at)
    {
        if (c.Thumb != null) return;   // 图片格没有文字
        int tx = c.Rect.Left + DraftStrip.IconPad + DraftStrip.IconSize + DraftStrip.TextGap;
        int tw = c.Rect.Right - DraftStrip.TextRight - tx;
        if (tw < 20) return;

        string name = DraftStrip.Ellipsize(DraftStrip.DisplayName(c.Src), SF.Get(9.5f), tw);
        TextRenderer.DrawText(dc, name, SF.Get(9.5f),
            new Rectangle(at.X + tx, at.Y + c.Rect.Top + 6, tw, 18),
            Theme.TextMain, TextFormatFlags.Left | TextFormatFlags.NoPrefix
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        string meta = DraftStrip.Ellipsize(AttachTypes.MetaOf(c.Src), SF.Get(8f), tw);
        if (meta.Length > 0)
            TextRenderer.DrawText(dc, meta, SF.Get(8f),
                new Rectangle(at.X + tx, at.Y + c.Rect.Top + 23, tw, 16),
                Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// 附件卡片的底色。跟着主题走，而且**必须和它身下的东西拉开距离**。
    ///
    /// 这张卡片画在**气泡上方**、压在对话区的底色上，所以「看得见」取决于它与
    /// <see cref="Theme.ChatBg"/> 的差 —— 而它旁边就是气泡本身，两张卡片一样深的话
    /// 附件和气泡会连成一片。这里仍按**气泡底色**混（两者差 17~20 级），
    /// 与气泡形成一套，同时和 ChatBg 也拉得开。
    /// </summary>
    private static Color ChipFill(Color bubble) => Theme.Mix(bubble, Theme.TextMain, 0.08f);

    /// <summary>
    /// 「思考过程」那块的**图形**部分（第一遍）：表头的箭头与文字、展开时的正文底板
    /// 与左侧竖线。正文文字在 <see cref="DrawReasonText"/>（第二遍）。
    /// </summary>
    private void DrawReasonBack(Graphics g, Color bubble, float vt, float vb)
    {
        if (_reasonH <= 0 || IsUser) return;
        string reason = Msg.Reasoning ?? "";
        if (reason.Length == 0) return;

        bool answering = (Msg.Text ?? "").Length > 0;
        string label = answering
            ? "思考过程" + (Msg.ReasoningMs > 0 ? " · " + Secs(Msg.ReasoningMs) : "")
            : "正在思考…";

        DrawFoldHeader(g, _reasonHeader, label, _reasonOpen, _reasonHover);
        if (_reasonOpen) DrawFoldBody(g, _reasonHeader.Y + ReasonHeadH + 6, _reasonBodyH, bubble, vt, vb);
    }

    /// <summary>
    /// 工具调用那块（第一遍：表头 + 展开时的底板）。
    ///
    /// 和思考块共用 <see cref="DrawFoldHeader"/> / <see cref="DrawFoldBody"/>：
    /// 这两块本来就是同一个形状（一行粗体小字 + 一个带竖线的旁白框），
    /// 各画一套的话箭头大小、文字基线、悬浮色的深浅会慢慢分家，
    /// 而那种差别摆在界面上没人说得清哪个才是对的。
    /// </summary>
    private void DrawToolBack(Graphics g, Color bubble, float vt, float vb)
    {
        if (_toolH <= 0 || IsUser) return;
        var calls = Msg.ToolCalls;
        if (calls == null || calls.Count == 0) return;

        DrawFoldHeader(g, _toolHeader, ToolLabel(), _toolOpen, _toolHover);
        if (!_toolOpen) return;

        float boxY = _toolHeader.Y + ReasonHeadH + 6;
        if (!DrawFoldBody(g, boxY, _toolBodyH, bubble, vt, vb)) return;

        // 每条调用左边一个小圆点，代替缩进 —— 纯靠缩进的话，换行后的结果文字
        // 和上一条的小标题会连成一片，分不清哪几行属于同一次调用。
        using var dot = new SolidBrush(Theme.Mix(bubble, Theme.Accent, 0.55f));
        foreach (var row in _toolRows)
        {
            float dy = boxY + ReasonPadY + row.Y + row.BriefH / 2f - 2;
            if (dy < vt - 4 || dy > vb + 4) continue;
            g.FillEllipse(dot, PadX + ReasonPadX + ReasonRuleW + 4, dy, 4f, 4f);
        }
    }

    /// <summary>
    /// 折叠块的**表头**：一个三角箭头 + 一行粗体小字，悬浮时整行变成主题色。
    ///
    /// 箭头用多边形而不是字符 —— 字符在不同字体下垂直居中的位置不一样，
    /// 表头会看着忽高忽低。
    /// </summary>
    private void DrawFoldHeader(Graphics g, Rectangle head, string label, bool open, bool hover)
    {
        Color ink = hover ? Theme.Accent : Theme.TextMuted;

        float ax = head.X + 3, ay = head.Y + ReasonHeadH / 2f;
        var tri = open
            ? new[] { new PointF(ax, ay - 2.5f), new PointF(ax + 7, ay - 2.5f), new PointF(ax + 3.5f, ay + 2.5f) }
            : new[] { new PointF(ax, ay - 3.5f), new PointF(ax + 5, ay), new PointF(ax, ay + 3.5f) };
        using (var tb = new SolidBrush(ink)) g.FillPolygon(tb, tri);

        using (var hf = Theme.UI(10f, FontStyle.Bold))
        using (var hb = new SolidBrush(ink))
            g.DrawString(label, hf, hb, head.X + 15, head.Y + 2);
    }

    /// <summary>
    /// 折叠块的**旁白框**（展开时才画）：一块浅色圆角底 + 左边一道竖线。
    /// 竖线是「这段是旁白、不是回答本身」的唯一标识，两行以上时全靠它区分。
    ///
    /// 返回 false 表示整个框都在可见带之外，调用方别再画里面的东西了。
    /// </summary>
    private bool DrawFoldBody(Graphics g, float boxY, float bodyH, Color bubble, float vt, float vb)
    {
        if (boxY + bodyH < vt || boxY > vb) return false;   // 整块在可见带外

        // 底色由调用方传进来（气泡自己的填充色）：这块画在气泡里面，
        // 混错基准就会在气泡上留一块异色的补丁。
        using (var box = RoundedRect(PadX, boxY, _inner - 1, bodyH - 1, 8))
        using (var b = new SolidBrush(Theme.Mix(bubble, Theme.TextMuted, 0.09f)))
            g.FillPath(b, box);

        using (var rule = new SolidBrush(Theme.Mix(bubble, Theme.Accent, 0.45f)))
            g.FillRectangle(rule, PadX + ReasonPadX, boxY + ReasonPadY - 2,
                            ReasonRuleW - 1, bodyH - ReasonPadY * 2 + 4);
        return true;
    }

    /// <summary>
    /// 思考正文的**文字**（第二遍）。用 <see cref="TextRenderer"/> 量、也用
    /// <see cref="TextRenderer"/> 画，两边同一套 flag —— 用 GDI+ 量、GDI 画
    /// （或者反过来）会差出一两行，盒子高度是对的、字却溢出去了。
    /// </summary>
    private void DrawReasonText(IDeviceContext dc, Point at, float vt, float vb)
    {
        if (_reasonH <= 0 || IsUser || !_reasonOpen) return;
        string reason = Msg.Reasoning ?? "";
        if (reason.Length == 0) return;

        float boxY = _reasonHeader.Y + ReasonHeadH + 6;
        if (boxY + _reasonBodyH < vt || boxY > vb) return;
        using var f = Theme.UI(10.5f);
        TextRenderer.DrawText(dc, reason, f,
            new Rectangle((int)(at.X + PadX + ReasonPadX + ReasonRuleW), (int)(at.Y + boxY + ReasonPadY),
                          ReasonTextW(), (int)(_reasonBodyH - ReasonPadY * 2)),
            Theme.TextMuted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// 工具块展开时的**文字**（第二遍）。每条调用两行：一行小标题（调了什么、花了多久），
    /// 一行结果预览。
    ///
    /// 位置全部读 <see cref="_toolRows"/> 里量好的几何，不在这里重算 ——
    /// 重算的话，量的那套和画的这套一旦分家，表现是最后一条的文字压出框外，
    /// 而两处看着都「没错」。
    /// </summary>
    private void DrawToolText(IDeviceContext dc, Point at, float vt, float vb)
    {
        if (_toolH <= 0 || IsUser || !_toolOpen || _toolRows.Count == 0) return;

        float boxY = _toolHeader.Y + ReasonHeadH + 6;
        if (boxY + _toolBodyH < vt || boxY > vb) return;

        int x = (int)(at.X + PadX + ReasonPadX + ReasonRuleW + ToolBulletW);
        int w = ReasonTextW() - ToolBulletW;
        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;

        using var fb = Theme.UI(10.5f);
        using var fp = Theme.UI(10f);
        foreach (var row in _toolRows)
        {
            // 局部 y 用来判可见带、绝对 y 用来画 —— 两个坐标系的数**不能混着比**：
            // vt/vb 是从 visTop/visBottom 减去 at.Y 得来的（局部），而画的时候
            // TextRenderer 要的是绝对坐标。拿绝对 y 去比局部 vt/vb，只要气泡不在
            // 视口最顶上，比较就恒为假，表现是「这一块只有圆点、一个字都没有」。
            float ly = boxY + ReasonPadY + row.Y;
            float ay = at.Y + ly;

            // 每条各自判一次可见带：一次读文件的结果预览可能比整个窗口还高，
            // 不判的话每次重画都要把它整段排一遍。
            if (ly + row.BriefH >= vt && ly <= vb)
                TextRenderer.DrawText(dc, row.Brief, fb, new Rectangle(x, (int)ay, w, (int)row.BriefH),
                                      Theme.TextMain, flags);

            if (row.PreviewH <= 0) continue;
            float py = ly + row.BriefH + 3;
            if (py + row.PreviewH < vt || py > vb) continue;
            TextRenderer.DrawText(dc, row.Preview, fp, new Rectangle(x, (int)(at.Y + py), w, (int)row.PreviewH),
                                  Theme.TextMuted, flags);
        }
    }

    /// <summary>
    /// 「回复被截断」那句的**底板**（第一遍）。放在正文**下面**而不是塞进正文里：
    /// 它不是模型说的话，混进正文还会被下一轮当上下文发回给模型。
    /// </summary>
    private void DrawWarningBack(Graphics g, Color bubble, float vt, float vb)
    {
        string warn = Msg.Warning ?? "";
        if (warn.Length == 0) return;

        float top = BodyBottom() + WarnGap;
        float h = WarnBoxH(warn);
        if (top + h < vt || top > vb) return;
        using (var box = RoundedRect(PadX, top, _inner - 1, h - 1, 8))
        using (var b = new SolidBrush(Theme.Mix(bubble, WarnInk, 0.14f)))
            g.FillPath(b, box);
        using (var pen = new Pen(Theme.Mix(bubble, WarnInk, 0.5f), 1f))
        using (var box2 = RoundedRect(PadX, top, _inner - 1, h - 1, 8))
            g.DrawPath(pen, box2);
    }

    /// <summary>截断说明的**文字**（第二遍）。</summary>
    private void DrawWarningText(IDeviceContext dc, Point at, float vt, float vb)
    {
        string warn = Msg.Warning ?? "";
        if (warn.Length == 0) return;

        float top = BodyBottom() + WarnGap;
        float h = WarnBoxH(warn);
        if (top + h < vt || top > vb) return;
        using var f = Theme.UI(10.5f);
        TextRenderer.DrawText(dc, warn, f,
            new Rectangle(at.X + PadX + WarnPad, (int)(at.Y + top + WarnPad),
                          _inner - WarnPad * 2, (int)(h - WarnPad * 2)),
            WarnInk, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    /// <summary>琥珀色，深浅两套主题各一个 —— 亮色下的深琥珀放到暗底上会糊成一团。</summary>
    private static Color WarnInk => Theme.Dark ? Color.FromArgb(232, 168, 82) : Color.FromArgb(176, 106, 12);

    private static string Secs(int ms) => (ms / 1000.0).ToString("0.0") + "s";

    internal static GraphicsPath RoundedRect(float x, float y, float w, float h, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        if (w <= 0 || h <= 0) { p.AddRectangle(new RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h))); return p; }
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
