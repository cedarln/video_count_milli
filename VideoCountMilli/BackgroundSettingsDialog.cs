using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VideoCountMilli;

/// <summary>
/// 背景设置对话框：为视频显示区选择纯色或图片背景，可恢复默认。
/// </summary>
public class BackgroundSettingsDialog : Form
{
    private Color _color;
    private Image? _image;      // 当前预览/结果图；_ownImage 表示由本对话框加载并负责释放
    private bool _ownImage;
    private bool _useImage;

    private readonly Panel _preview;

    /// <summary>选定的背景色（图片模式下作为图片未覆盖区域的兜底色）。</summary>
    public Color SelectedColor => _color;

    /// <summary>选定的背景图；null 表示纯色背景。</summary>
    public Image? SelectedImage => _useImage ? _image : null;

    public BackgroundSettingsDialog(Color currentColor, Image? currentImage)
    {
        _color = currentColor;
        _image = currentImage;
        _ownImage = false;
        _useImage = currentImage != null;

        Text = "背景设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(430, 200);
        Font = new Font("Segoe UI", 9F);
        Theme.DarkForm(this);

        var lbl = new Label
        {
            Text = "预览：",
            Location = new Point(12, 14),
            AutoSize = true
        };

        _preview = new Panel
        {
            Location = new Point(12, 36),
            Size = new Size(200, 118),
            BorderStyle = BorderStyle.FixedSingle
        };

        var btnPickColor = new Button { Text = "选择颜色…", Location = new Point(228, 36), Size = new Size(92, 30) };
        var btnPickImage = new Button { Text = "选择图片…", Location = new Point(228, 72), Size = new Size(92, 30) };
        var btnReset = new Button { Text = "恢复默认", Location = new Point(228, 108), Size = new Size(92, 30) };
        Theme.StyleNeutral(btnPickColor);
        btnPickColor.Click += (s, e) => PickColor();
        Theme.StyleNeutral(btnPickImage);
        btnPickImage.Click += (s, e) => PickImage();
        Theme.StyleNeutral(btnReset);
        btnReset.Click += (s, e) =>
        {
            SetImage(null, owned: false);
            _color = Theme.Window;
            _useImage = false;
            ApplyPreview();
        };

        var btnOk = new Button { Text = "确定", Location = new Point(238, 162), Size = new Size(82, 30) };
        var btnCancel = new Button { Text = "取消", Location = new Point(330, 162), Size = new Size(82, 30) };
        Theme.StyleAccent(btnOk);
        btnOk.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
        Theme.StyleNeutral(btnCancel);
        btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[] { lbl, _preview, btnPickColor, btnPickImage, btnReset, btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        ApplyPreview();

        // 取消时释放本对话框加载的图片
        FormClosed += (s, e) =>
        {
            if (DialogResult != DialogResult.OK && _ownImage)
            {
                _image?.Dispose();
                _image = null;
            }
        };
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog
        {
            Color = _color,
            FullOpen = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            SetImage(null, owned: false);
            _color = dlg.Color;
            _useImage = false;
            ApplyPreview();
        }
    }

    private void PickImage()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif"
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            using var tmp = Image.FromFile(ofd.FileName);
            var bmp = new Bitmap(tmp); // 复制一份，避免长时间锁定图片文件
            SetImage(bmp, owned: true);
            _useImage = true;
            ApplyPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法加载图片: {ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetImage(Image? img, bool owned)
    {
        if (_ownImage)
        {
            _image?.Dispose();
        }
        _image = img;
        _ownImage = owned;
    }

    private void ApplyPreview()
    {
        _preview.BackgroundImage = _useImage ? _image : null;
        _preview.BackgroundImageLayout = ImageLayout.Zoom;
        _preview.BackColor = _useImage ? Theme.Window : _color;
    }
}
