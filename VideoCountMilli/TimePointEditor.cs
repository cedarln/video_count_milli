using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoCountMilli;

/// <summary>
/// 时间点编辑对话框：支持直接输入 HH:MM:SS.mmm，
/// 以及按帧（±1帧）/毫秒（±1、±10毫秒）微调，并可清除时间点。
/// </summary>
public class TimePointEditor : Form
{
    private readonly TextBox _txtTime;
    private readonly long _durationMs;
    private readonly double _frameMs;
    private long _value;

    /// <summary>结果值；为 null 表示清除该时间点。</summary>
    public long? ResultValue { get; private set; }

    public TimePointEditor(string title, long initialMs, double frameDurationMs, long durationMs)
    {
        _frameMs = frameDurationMs > 0 ? frameDurationMs : 40.0;
        _durationMs = durationMs;
        _value = Math.Clamp(initialMs, 0, Math.Max(0, durationMs));

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(384, 178);
        Font = new Font("Segoe UI", 9F);

        var lbl = new Label
        {
            Text = "时间（时:分:秒.毫秒）：",
            Location = new Point(12, 14),
            AutoSize = true
        };

        _txtTime = new TextBox
        {
            Location = new Point(12, 36),
            Width = 168,
            Font = new Font("Consolas", 11F),
            Text = TimeFormatter.Format(_value)
        };
        _txtTime.KeyDown += TxtTime_KeyDown;

        // 微调按钮（两行三列）
        string[] labels = { "-1帧", "+1帧", "-10ms", "+10ms", "-1ms", "+1ms" };
        long[] deltas =
        {
            -(long)Math.Round(_frameMs), (long)Math.Round(_frameMs),
            -10, 10, -1, 1
        };
        for (int i = 0; i < labels.Length; i++)
        {
            int col = i % 3, row = i / 3;
            var b = new Button
            {
                Text = labels[i],
                Location = new Point(188 + col * 64, 34 + row * 31),
                Size = new Size(60, 27),
                FlatStyle = FlatStyle.Flat,
                Tag = deltas[i]
            };
            b.Click += Nudge_Click;
            Controls.Add(b);
        }

        var lblHint = new Label
        {
            Text = "可点击右侧按钮微调，或直接输入时间后回车。\n范围：00:00:00.000 ~ " + TimeFormatter.Format(_durationMs),
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8F),
            Location = new Point(12, 68),
            Size = new Size(168, 34)
        };

        var btnClear = new Button
        {
            Text = "清除",
            Location = new Point(12, 110),
            Size = new Size(75, 30),
            FlatStyle = FlatStyle.Flat
        };
        btnClear.Click += (s, e) =>
        {
            ResultValue = null;
            DialogResult = DialogResult.OK;
            Close();
        };

        var btnOk = new Button
        {
            Text = "确定",
            Location = new Point(216, 110),
            Size = new Size(75, 30)
        };
        btnOk.Click += (s, e) => ApplyAndClose();

        var btnCancel = new Button
        {
            Text = "取消",
            Location = new Point(300, 110),
            Size = new Size(75, 30)
        };
        btnCancel.Click += (s, e) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.AddRange(new Control[] { lbl, _txtTime, lblHint, btnClear, btnOk, btnCancel });

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void TxtTime_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ApplyAndClose();
        }
    }

    private void Nudge_Click(object? sender, EventArgs e)
    {
        long delta = (long)((Button)sender!).Tag!;
        _value = Math.Clamp(_value + delta, 0, Math.Max(0, _durationMs));
        _txtTime.Text = TimeFormatter.Format(_value);
        _txtTime.Focus();
        _txtTime.SelectAll();
    }

    private void ApplyAndClose()
    {
        if (TimeFormatter.TryParse(_txtTime.Text, out long ms))
        {
            _value = Math.Clamp(ms, 0, Math.Max(0, _durationMs));
            ResultValue = _value;
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            MessageBox.Show(this,
                "时间格式无效，请使用 HH:MM:SS.mmm 格式（如 00:29:59.128）。",
                "格式错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtTime.Focus();
            _txtTime.SelectAll();
        }
    }
}
