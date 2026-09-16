
namespace BangGang;

/// <summary>主题变化时可自我刷新的控件。</summary>
internal interface IThemed
{
    void Restyle();
}

/// <summary>需要显式排布的控件（WinForms 会跳过不可见控件的自动布局，因此这里手动排布）。</summary>
internal interface IArranged
{
    void Arrange();
}
