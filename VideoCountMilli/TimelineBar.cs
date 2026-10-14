using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VideoCountMilli;

/// <summary>
/// 自定义时间轴控件：显示播放进度、可拖动的起点/终点标记，
/// 支持点击/拖动定位。拖动过程中通过 <see cref="SeekPreview"/> 实时回调，
/// 便于在定位时预览对应画面帧。
/// </summary>
public sealed class TimelineBar : Control
{
    private const int Pad = 10;        // 左右内边距（给标记留出空间）
    private const int MarkerHit = 10;  // 标记热区半径（像素）

    private long _maximum = 1;
    private long _value;
    private long? _startMarker;
    private long? _endMarker;

    private enum DragTarget { None, Playhead, StartMarker, EndMarker }
    private DragTarget _drag = DragTarget.None;

    /// <summary>定位到某毫秒（拖动或点击时触发，用于实时画面预览）。</summary>
    public event Action<long>? SeekPreview;

    /// <summary>一次拖动/点击定位结束（松手）。</summary>
    public event Action? SeekCompleted;

    /// <summary>开始拖动/点击定位。</summary>
    public event Action? DragStarted;

    /// <summary>起点标记被拖动到新位置。</summary>
    public event Action<long>? StartMarkerMoved;

    /// <summary>终点标记被拖动到新位置。</summary>
    public event Action<long>? EndMarkerMoved;

    public TimelineBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(40, 40, 40);
        Height = 46;
        TabStop = false;
    }

    /// <summary>时间轴最大值（毫秒），通常等于视频时长。</summary>
    public long Maximum
    {
        get => _maximum;
        set { _maximum = Math.Max(1, value); Invalidate(); }
    }

    /// <summary>当前播放位置（毫秒）。</summary>
    public long Value
    {
        get => _value;
        set { _value = Clamp(value); Invalidate(); }
    }

    /// <summary>起点标记位置（毫秒），null 表示未设置。</summary>
    public long? StartMarker
    {
        get => _startMarker;
        set { _startMarker = value.HasValue ? Clamp(value.Value) : null; Invalidate(); }
    }

    /// <summary>终点标记位置（毫秒），null 表示未设置。</summary>
    public long? EndMarker
    {
        get => _endMarker;
        set { _endMarker = value.HasValue ? Clamp(value.Value) : null; Invalidate(); }
    }

    private long Clamp(long v) => Math.Max(0, Math.Min(_maximum, v));

    private int XFromValue(long v)
    {
        int left = Pad;
        int span = Math.Max(1, Width - Pad * 2);
        return left + (int)Math.Round((double)Clamp(v) / _maximum * span);
    }

    private long ValueFromX(int x)
    {
        int left = Pad;
        int span = Math.Max(1, Width - Pad * 2);
        double ratio = (double)(x - left) / span;
        ratio = Math.Max(0.0, Math.Min(1.0, ratio));
        return (long)Math.Round(ratio * _maximum);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _maximum <= 0) return;

        if (_startMarker.HasValue && Math.Abs(e.X - XFromValue(_startMarker.Value)) <= MarkerHit)
        {
            _drag = DragTarget.StartMarker;
            DragStarted?.Invoke();
            SeekPreview?.Invoke(_startMarker.Value);
        }
        else if (_endMarker.HasValue && Math.Abs(e.X - XFromValue(_endMarker.Value)) <= MarkerHit)
        {
            _drag = DragTarget.EndMarker;
            DragStarted?.Invoke();
            SeekPreview?.Invoke(_endMarker.Value);
        }
        else
        {
            _drag = DragTarget.Playhead;
            DragStarted?.Invoke();
            Value = ValueFromX(e.X);
            SeekPreview?.Invoke(_value);
        }
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == DragTarget.None) return;

        long v = ValueFromX(e.X);
        switch (_drag)
        {
            case DragTarget.Playhead:
                Value = v;
                SeekPreview?.Invoke(v);
                break;
            case DragTarget.StartMarker:
                _startMarker = v;
                Invalidate();
                StartMarkerMoved?.Invoke(v);
                SeekPreview?.Invoke(v);
                break;
            case DragTarget.EndMarker:
                _endMarker = v;
                Invalidate();
                EndMarkerMoved?.Invoke(v);
                SeekPreview?.Invoke(v);
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragTarget.None) return;
        _drag = DragTarget.None;
        Capture = false;
        SeekCompleted?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int left = Pad;
        int span = Math.Max(1, Width - Pad * 2);
        int barH = 6;
        int barY = Height / 2;

        // 轨道背景
        using (var brush = new SolidBrush(Color.FromArgb(70, 70, 70)))
        {
            g.FillRectangle(brush, left, barY, span, barH);
        }

        // 循环区间高亮（起点与终点之间）
        if (_startMarker.HasValue && _endMarker.HasValue)
        {
            int ax = XFromValue(Math.Min(_startMarker.Value, _endMarker.Value));
            int bx = XFromValue(Math.Max(_startMarker.Value, _endMarker.Value));
            if (bx > ax)
            {
                using var brush = new SolidBrush(Color.FromArgb(90, 0, 200, 0));
                g.FillRectangle(brush, ax, barY, bx - ax, barH);
            }
        }

        // 已播放部分
        int vx = XFromValue(_value);
        if (vx > left)
        {
            using var brush = new SolidBrush(Color.FromArgb(0, 120, 215));
            g.FillRectangle(brush, left, barY, vx - left, barH);
        }

        // 起点/终点标记（画在轨道上方，三角形指向轨道）
        DrawMarker(g, _startMarker, Color.DeepSkyBlue, barY);
        DrawMarker(g, _endMarker, Color.Orange, barY);

        // 当前位置指示线
        using (var pen = new Pen(Color.White, 2))
        {
            g.DrawLine(pen, vx, barY - 6, vx, barY + barH + 6);
        }
    }

    private void DrawMarker(Graphics g, long? marker, Color color, int barY)
    {
        if (!marker.HasValue) return;
        int x = XFromValue(marker.Value);
        Point[] pts =
        {
            new(x - 6, barY - 13),
            new(x + 6, barY - 13),
            new(x, barY - 1)
        };
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, pts);
    }
}
