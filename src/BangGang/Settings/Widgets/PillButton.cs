using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>圆角胶囊按钮；<see cref="On"/> 为 false 时呈禁用态且不响应点击（用于“内容变化后才可保存”）。</summary>
internal sealed class PillButton : Control, IThemed
{
    internal enum Look { Primary, Ghost, Danger }

    private bool _hover, _pressed, _on = true;

    public Look Kind { get; set; }

    public bool On
    {
        get => _on;
        set { if (_on == value) return; _on = value; Invalidate(); }
    }

    public PillButton(string text, Look kind = Look.Primary, int w = 104, int h = 38)
    {
        Kind = kind;
        Size = new Size(w, h);
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
    }

    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set { base.Text = value ?? ""; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    /// <summary>禁用态下吞掉点击事件。</summary>
    protected override void OnClick(EventArgs e)
    {
        if (!_on) return;
        base.OnClick(e);
    }

    /// <summary>键盘（空格 / 回车）等效于点击，便于用键盘完成保存。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Focused && _on && (keyData == Keys.Space || keyData == Keys.Enter))
        {
            OnClick(EventArgs.Empty);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void Restyle() { BackColor = SC.CardBg; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        int rad = Math.Min(Height / 2, 12);

        Color fill, ink;
        switch (Kind)
        {
            case Look.Danger:
                fill = _hover ? SC.Mix(SC.Danger, Color.White, 0.10f) : SC.Danger;
                ink = Color.White;
                break;
            case Look.Ghost:
                fill = _hover ? SC.Mix(SC.CardBg, Theme.Border, 0.45f) : SC.CardBg;
                ink = _on ? SC.Ink : SC.InkFaint;
                break;
            default:
                if (_on)
                {
                    fill = _hover ? SC.Mix(Theme.Accent, Color.White, 0.12f) : Theme.Accent;
                    ink = Color.White;
                }
                else
                {
                    // 禁用态：底色与文字都保留可读的对比度，一眼能看出“不可点击”
                    fill = SC.Mix(SC.CardBg, Theme.Border, 0.85f);
                    ink = SC.Mix(SC.InkMuted, SC.CardBg, 0.18f);
                }
                break;
        }
        if (_pressed && _on && Kind != Look.Ghost) fill = SC.Mix(fill, Color.Black, 0.06f);

        RP.Fill(g, rc, rad, fill);
        if (Kind == Look.Ghost) RP.Stroke(g, rc, rad, _hover ? SC.Accent : SC.FieldBorder);
        TextRenderer.DrawText(g, Text, SF.Get(11f), rc, ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        base.OnPaint(e);
    }
}

/// <summary>左侧菜单项：图标 + 文字，可带未保存小圆点。</summary>
