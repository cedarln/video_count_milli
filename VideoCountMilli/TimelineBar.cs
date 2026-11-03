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
        BackColor = Theme.Panel;
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
        int barH = 8;
        int barY = (Height - barH) / 2;

        // 轨道背景
        using (var brush = new SolidBrush(Theme.Track))
        {
            g.FillRectangle(brush, left, barY, span, barH);
        }

        // 已播放部分
        int vx = XFromValue(_value);
        if (vx > left)
        {
            using var brush = new SolidBrush(Theme.Played);
            g.FillRectangle(brush, left, barY, vx - left, barH);
        }

        // 循环区间高亮（起点与终点之间，半透明压在轨道与进度之上）
        if (_startMarker.HasValue && _endMarker.HasValue)
        {
            int ax = XFromValue(Math.Min(_startMarker.Value, _endMarker.Value));
            int bx = XFromValue(Math.Max(_startMarker.Value, _endMarker.Value));
            if (bx > ax)
            {
                using var brush = new SolidBrush(Theme.RegionTint);
                g.FillRectangle(brush, ax, barY - 2, bx - ax, barH + 4);
            }
        }

        DrawTicks(g, barY, barH);

        // 起点/终点标记（画在轨道上方，三角形指向轨道）
        DrawMarker(g, _startMarker, Theme.Accent, barY);
        DrawMarker(g, _endMarker, Theme.Warm, barY);

        // 当前位置指示：亮色主体 + 一圈暗边，保证压在任意底色上都看得清
        int top = barY - 9;
        int bottom = barY + barH + 9;
        using (var edge = new Pen(Theme.Window, 5))
        {
            g.DrawLine(edge, vx, top, vx, bottom);
        }
        using (var head = new Pen(Theme.Playhead, 3))
        {
            g.DrawLine(head, vx, top, vx, bottom);
        }
    }

    /// <summary>
    /// 画刻度线。步长按当前时长与宽度自适应（1s ~ 10min 之间的整值），
    /// 目标是刻度间距不小于 24px，避免短视频密成一片。
    /// </summary>
    private void DrawTicks(Graphics g, int barY, int barH)
    {
        if (_maximum <= 0) return;

        long step = TickStepMs(_maximum, Math.Max(1, Width - Pad * 2));
        if (step <= 0) return;

        using var pen = new Pen(Theme.Tick);
        int y0 = barY + barH + 2;
        for (long t = step; t < _maximum; t += step)
        {
            int x = XFromValue(t);
            g.DrawLine(pen, x, y0, x, y0 + 3);
        }
    }

    private static long TickStepMs(long durationMs, int spanPx)
    {
        long[] candidates = { 1000, 2000, 5000, 10_000, 15_000, 30_000, 60_000, 120_000, 300_000, 600_000 };
        foreach (long step in candidates)
        {
            double pxPerTick = (double)spanPx * step / Math.Max(1, durationMs);
            if (pxPerTick >= 24) return step;
        }
        return 0; // 超过 10 分钟刻度仍太密时不画
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
