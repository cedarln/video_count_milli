using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoCountMilli;

/// <summary>
/// 暗色配色表：界面用到的颜色集中在这里定义，控件代码只引用语义名，
/// 不再散落 Color.FromArgb 字面量。只描述外观，不涉及任何布局尺寸。
/// </summary>
internal static class Theme
{
    #region 表面

    /// <summary>窗口与视频显示区底色。</summary>
    public static readonly Color Window = Hex("0B0C0E");

    /// <summary>控制面板、记录面板底色。</summary>
    public static readonly Color Panel = Hex("232629");

    /// <summary>标题栏、列表列头等凸起表面。</summary>
    public static readonly Color Raised = Hex("2C3035");

    /// <summary>列表底色。</summary>
    public static readonly Color ListBackground = Hex("1A1D20");

    /// <summary>列表斑马纹行底色（ListBackground 上叠 3% 白）。</summary>
    public static readonly Color ListZebra = Hex("212427");

    /// <summary>网格线、分隔线。</summary>
    public static readonly Color ListGrid = Hex("3A4046");

    /// <summary>控件描边。</summary>
    public static readonly Color Border = Hex("4A5058");

    #endregion

    #region 文字

    /// <summary>主文字与读数。</summary>
    public static readonly Color Text = Hex("E8EAED");

    /// <summary>次级文字。</summary>
    public static readonly Color TextSecondary = Hex("B4B8BD");

    /// <summary>提示性文字（对白底对比度约 4.6:1，旧 DimGray 只有 2.4:1）。</summary>
    public static readonly Color TextMuted = Hex("8A9096");

    #endregion

    #region 按钮

    public static readonly Color ButtonFace = Hex("3A3F45");
    public static readonly Color ButtonHover = Hex("474D54");
    public static readonly Color ButtonPressed = Hex("2F3438");

    public static readonly Color ButtonDisabledFace = Hex("2B2F33");
    public static readonly Color ButtonDisabledBorder = Hex("33383D");
    public static readonly Color ButtonDisabledText = Hex("6B7178");

    /// <summary>主强调色（起点按钮、已播放进度、选中行）。</summary>
    public static readonly Color Accent = Hex("4DA3FF");
    public static readonly Color AccentHover = Hex("63B0FF");
    public static readonly Color AccentPressed = Hex("3A82CE");

    /// <summary>终点相关：文字用 Warm，按钮底色用 WarmFace。</summary>
    public static readonly Color Warm = Hex("FF9A3C");
    public static readonly Color WarmFace = Hex("B9601F");
    public static readonly Color WarmHover = Hex("D4741F");
    public static readonly Color WarmPressed = Hex("9A4E16");

    /// <summary>耗时读数、成功状态。</summary>
    public static readonly Color Success = Hex("7BE495");

    #endregion

    #region 时间轴

    public static readonly Color Track = Hex("3A3F45");
    public static readonly Color Played = Hex("4DA3FF");

    /// <summary>起终点区间底色（Accent 20% 不透明）。</summary>
    public static readonly Color RegionTint = Color.FromArgb(51, 77, 163, 255);

    public static readonly Color Playhead = Hex("E8EAED");

    /// <summary>分钟刻度。</summary>
    public static readonly Color Tick = Hex("454B52");

    #endregion

    /// <summary>从 #RRGGBB 字符串构造颜色（不含 #）。</summary>
    public static Color Hex(string rrggbb)
    {
        if (rrggbb.Length != 6) throw new ArgumentException("需要 6 位十六进制", nameof(rrggbb));
        return Color.FromArgb(
            Convert.ToByte(rrggbb.Substring(0, 2), 16),
            Convert.ToByte(rrggbb.Substring(2, 2), 16),
            Convert.ToByte(rrggbb.Substring(4, 2), 16));
    }

    /// <summary>
    /// 把按钮刷成扁平暗色，并接好悬停 / 按下 / 禁用三态。
    /// 禁用时换成低饱和底色与灰字，避免 WinForms 默认在深色底上灰化文字后看不清。
    /// </summary>
    public static Button FlatButton(
        Button button,
        Color face,
        Color text,
        Color border,
        Color hover,
        Color pressed)
    {
        button.FlatStyle = FlatStyle.Flat;

        void ApplyState()
        {
            if (button.Enabled)
            {
                button.BackColor = face;
                button.ForeColor = text;
                button.FlatAppearance.BorderColor = border;
                button.FlatAppearance.MouseOverBackColor = hover;
                button.FlatAppearance.MouseDownBackColor = pressed;
            }
            else
            {
                button.BackColor = ButtonDisabledFace;
                button.ForeColor = ButtonDisabledText;
                button.FlatAppearance.BorderColor = ButtonDisabledBorder;
                button.FlatAppearance.MouseOverBackColor = ButtonDisabledFace;
                button.FlatAppearance.MouseDownBackColor = ButtonDisabledFace;
            }
        }

        button.FlatAppearance.BorderSize = 1;
        button.EnabledChanged += (s, e) => ApplyState();
        ApplyState();
        return button;
    }

    /// <summary>
    /// 把对话框刷成与主界面一致的暗色（只改颜色，不动布局与控件位置）。
    /// 标签、按钮文字未显式指定颜色时继承窗体的 ForeColor。
    /// </summary>
    public static void DarkForm(Form form)
    {
        form.BackColor = Panel;
        form.ForeColor = Text;
    }

    /// <summary>文本框暗色：深底亮字，描边用统一边框色。</summary>
    public static void DarkTextBox(TextBox box)
    {
        box.BackColor = ListBackground;
        box.ForeColor = Text;
    }

    /// <summary>普通功能按钮（灰底白字）。</summary>
    public static Button StyleNeutral(Button button) =>
        FlatButton(button, ButtonFace, Text, Border, ButtonHover, ButtonPressed);

    /// <summary>主操作按钮（蓝底）。</summary>
    public static Button StyleAccent(Button button) =>
        FlatButton(button, Accent, Window, Accent, AccentHover, AccentPressed);

    /// <summary>终点相关按钮（橙底）。</summary>
    public static Button StyleWarm(Button button) =>
        FlatButton(button, WarmFace, Text, Warm, WarmHover, WarmPressed);
}
