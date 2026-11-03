using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace VideoCountMilli;

public partial class MainForm : Form
{
    private readonly FFmpegPlayer _player;
    private long? _startPointMs;
    private long? _endPointMs;
    private bool _isDraggingTimeline;
    private bool _fileOpened;
    private string? _currentVideoPath;
    private Bitmap? _currentFrame;
    private readonly object _frameLock = new();

    // 拖动时间轴时的实时画面预览（后台线程解码，只追最新位置）
    private bool _wasPlayingBeforeDrag;
    private long _dragPreviewMs = -1;
    private long _lastPreviewRequestTick;
    private int _dragSession;                  // 拖动轮次，防止上一轮的收尾踩到下一轮
    private long _displayedFrameSeq;           // 已显示帧的出帧序号

    /// <summary>拖动时向预览线程提目标的最小间隔：文字始终跟手，画面按此限速。</summary>
    private const int PreviewMinIntervalMs = 25;

    // 音量 / 全屏
    private double _volume = 1.0;
    private bool _isFullscreen;
    private FormBorderStyle _prevBorderStyle;
    private FormWindowState _prevWindowState;
    private Rectangle _prevBounds;

    // 右侧测量记录面板折叠状态
    private const int HistoryExpandedWidth = 340;
    private const int HistoryCollapsedWidth = 30;
    private bool _historyCollapsed;

    // 背景设置
    private string? _backgroundImagePath;
    private bool _updateInProgress;

    public MainForm()
    {
        InitializeComponent();
        ApplyTheme();
        _player = new FFmpegPlayer();

        // 绑定播放器事件
        _player.FrameDecoded += OnFrameDecoded;
        _player.PositionChanged += OnPositionChanged;
        _player.DurationChanged += OnDurationChanged;
        _player.PlayStateChanged += OnPlayStateChanged;
        _player.PlaybackEnded += OnPlaybackEnded;
        _player.Error += OnPlayerError;

        // 播放控制按钮
        _btnOpen.Click += BtnOpen_Click;
        _btnPlay.Click += (s, e) => _player.Play();
        _btnPause.Click += (s, e) => _player.Pause();
        _btnStop.Click += (s, e) => _player.Stop();
        _btnSetStart.Click += (s, e) => SetStartPoint(_player.CurrentPositionMs);
        _btnSetEnd.Click += (s, e) => SetEndPoint(_player.CurrentPositionMs);
        _btnPrevFrame.Click += (s, e) => _player.StepFrame(-1);
        _btnNextFrame.Click += (s, e) => _player.StepFrame(1);
        _btnSnapshot.Click += (s, e) => SaveSnapshot();
        _btnFullscreen.Click += (s, e) => ToggleFullscreen();
        _chkTopMost.CheckedChanged += (s, e) => this.TopMost = _chkTopMost.Checked;

        // 倍速 / 音量 / 静音
        _cboSpeed.Items.AddRange(new object[]
        {
            "0.25x", "0.5x", "0.75x", "1x", "1.25x", "1.5x", "2x", "3x", "4x"
        });
        _cboSpeed.SelectedIndex = 3;
        _cboSpeed.SelectedIndexChanged += CboSpeed_SelectedIndexChanged;
        _trkVolume.Scroll += (s, e) => ApplyVolumeFromTrack();
        _chkMute.CheckedChanged += (s, e) => _player.SetMuted(_chkMute.Checked);
        this.MouseWheel += MainForm_MouseWheel;

        // 时间点编辑（按钮或点击时间文字）
        _btnEditStart.Click += (s, e) => EditStartPoint();
        _btnEditEnd.Click += (s, e) => EditEndPoint();
        _lblStartTime.Click += (s, e) => EditStartPoint();
        _lblEndTime.Click += (s, e) => EditEndPoint();

        // 跳转到起点/终点
        _btnJumpStart.Click += (s, e) => JumpToPoint(_startPointMs);
        _btnJumpEnd.Click += (s, e) => JumpToPoint(_endPointMs);

        // 时间轴事件（点击/拖动定位 + 起终点标记）
        _timeline.DragStarted += Timeline_DragStarted;
        _timeline.SeekPreview += Timeline_SeekPreview;
        _timeline.SeekCompleted += Timeline_SeekCompleted;
        _timeline.StartMarkerMoved += ms => SetStartPoint(ms);
        _timeline.EndMarkerMoved += ms => SetEndPoint(ms);

        // 测量记录
        _btnAddMeasure.Click += (s, e) => AddMeasurement();
        _btnDelMeasure.Click += (s, e) => DeleteSelectedMeasurements();
        _btnClearMeasure.Click += (s, e) => ClearMeasurements();
        _btnCopyMeasure.Click += (s, e) => CopyMeasurements();
        _btnExportMeasure.Click += (s, e) => ExportMeasurements();
        _btnToggleHistory.Click += (s, e) => SetHistoryCollapsed(!_historyCollapsed);
        _lvHistory.MouseDoubleClick += (s, e) => JumpToMeasurementStart();

        // 更新检查 / 背景设置按钮
        _btnUpdate.Click += BtnUpdate_Click;
        _btnBackground.Click += BtnBackground_Click;

        // 拖放文件支持
        this.AllowDrop = true;
        this.DragEnter += MainForm_DragEnter;
        this.DragDrop += MainForm_DragDrop;

        // 键盘快捷键
        this.KeyPreview = true;
        this.KeyDown += MainForm_KeyDown;

        this.FormClosing += MainForm_FormClosing;

        // 测量记录面板默认收起
        SetHistoryCollapsed(true);

        // 显式校正时间轴宽度，确保时间轴右边界与窗口右边距一致（修复越界）
        _timeline.Width = _controlPanel.ClientSize.Width - 24;

        // 加载用户设置（背景）
        LoadSettings();

        // 窗口 / 任务栏图标（嵌入资源，资源名 = 默认命名空间.相对路径）
        try
        {
            using var iconStream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("VideoCountMilli.Assets.app.ico");
            if (iconStream != null)
            {
                Icon = new Icon(iconStream);
            }
        }
        catch
        {
            // 图标加载失败时使用默认图标，不影响运行
        }
    }

    #region 界面配色

    /// <summary>
    /// 应用暗色配色（色值集中定义在 Theme）。Designer 里只写结构、尺寸与字体，
    /// 所有颜色在这里统一赋值，换皮肤不会牵动布局。
    /// </summary>
    private void ApplyTheme()
    {
        BackColor = Theme.Window;
        _videoPictureBox.BackColor = Theme.Window;

        _controlPanel.BackColor = Theme.Panel;
        _historyPanel.BackColor = Theme.Panel;
        _historyButtons.BackColor = Theme.Panel;
        _historyHeader.BackColor = Theme.Raised;

        _lblFileInfo.ForeColor = Theme.TextSecondary;
        _lblCurrentTime.ForeColor = Theme.Text;
        _lblDuration.ForeColor = Theme.TextSecondary;
        _lblSep.ForeColor = Theme.TextMuted;
        _lblStartTime.ForeColor = Theme.Accent;
        _lblEndTime.ForeColor = Theme.Warm;
        _lblSegmentDuration.ForeColor = Theme.Success;
        _lblShortcuts.ForeColor = Theme.TextMuted;
        _lblSpeed.ForeColor = Theme.TextSecondary;
        _lblVolume.ForeColor = Theme.TextSecondary;
        _lblHistoryTitle.ForeColor = Theme.Text;

        foreach (var cb in new[] { _chkTopMost, _chkMute, _chkLoopRegion })
        {
            cb.ForeColor = Theme.Text;
            cb.BackColor = Theme.Panel;
        }

        _cboSpeed.FlatStyle = FlatStyle.Flat;
        _cboSpeed.BackColor = Theme.ButtonFace;
        _cboSpeed.ForeColor = Theme.Text;
        _trkVolume.BackColor = Theme.Panel;

        // 主操作：起点相关用强调蓝，终点相关用橙，其余为中性灰
        Theme.StyleAccent(_btnSetStart);
        Theme.StyleAccent(_btnAddMeasure);
        Theme.StyleWarm(_btnSetEnd);
        foreach (var b in new[]
        {
            _btnOpen, _btnPlay, _btnPause, _btnStop, _btnPrevFrame, _btnNextFrame,
            _btnSnapshot, _btnFullscreen, _btnUpdate, _btnBackground,
            _btnEditStart, _btnEditEnd, _btnJumpStart, _btnJumpEnd, _btnToggleHistory,
            _btnDelMeasure, _btnClearMeasure, _btnCopyMeasure, _btnExportMeasure
        })
        {
            Theme.StyleNeutral(b);
        }

        ApplyHistoryListTheme();
    }

    /// <summary>
    /// 测量记录列表自绘：原生 ListView 在暗色下会留白底列头和亮色选中条，
    /// 因此接管绘制，配色跟随 Theme，并给奇偶行加斑马纹。
    /// </summary>
    private void ApplyHistoryListTheme()
    {
        _lvHistory.BackColor = Theme.ListBackground;
        _lvHistory.ForeColor = Theme.Text;
        _lvHistory.OwnerDraw = true;
        _lvHistory.DrawColumnHeader += LvHistory_DrawColumnHeader;
        _lvHistory.DrawItem += (s, e) => e.DrawDefault = false;
        _lvHistory.DrawSubItem += LvHistory_DrawSubItem;
    }

    private void LvHistory_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        Graphics g = e.Graphics;
        using (var back = new SolidBrush(Theme.Raised))
        {
            g.FillRectangle(back, e.Bounds);
        }
        var textRect = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height);
        TextRenderer.DrawText(g, e.Header?.Text ?? "", e.Font ?? _lvHistory.Font, textRect, Theme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        using (var pen = new Pen(Theme.ListGrid))
        {
            g.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 3);
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
        }
    }

    private void LvHistory_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        ListViewItem? item = e.Item;
        Graphics g = e.Graphics;
        bool selected = item?.Selected == true;
        Color backColor = selected
            ? Theme.Accent
            : (item?.Index ?? 0) % 2 == 1 ? Theme.ListZebra : Theme.ListBackground;

        // FullRowSelect 下最后一列右侧不会被逐个回调覆盖，首列时先把整行铺满
        if (e.ColumnIndex == 0 && item != null)
        {
            using var rowBrush = new SolidBrush(backColor);
            Rectangle row = item.Bounds;
            g.FillRectangle(rowBrush, 0, row.Y, _lvHistory.ClientSize.Width, row.Height);
        }

        using (var brush = new SolidBrush(backColor))
        {
            g.FillRectangle(brush, e.Bounds);
        }

        Color foreColor = selected
            ? Theme.Window
            : e.ColumnIndex == _lvHistory.Columns.Count - 1 ? Theme.Success : Theme.Text;

        var textRect = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height);
        TextRenderer.DrawText(g, e.SubItem?.Text ?? "", e.SubItem?.Font ?? _lvHistory.Font, textRect, foreColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        using (var pen = new Pen(Theme.ListGrid))
        {
            g.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
        }
    }

    #endregion

    #region 文件操作

    private void BtnOpen_Click(object? sender, EventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Title = "选择视频文件",
            Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.flv;*.wmv;*.m4v;*.ts;*.webm|" +
                     "所有文件|*.*"
        };

        if (ofd.ShowDialog() == DialogResult.OK)
        {
            OpenVideoFile(ofd.FileName);
        }
    }

    private void MainForm_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data!.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private void MainForm_DragDrop(object? sender, DragEventArgs e)
    {
        var files = (string[]?)e.Data!.GetData(DataFormats.FileDrop);
        if (files != null && files.Length > 0)
        {
            OpenVideoFile(files[0]);
        }
    }

    private void OpenVideoFile(string filePath)
    {
        try
        {
            this.Cursor = Cursors.WaitCursor;

            // 重置时间点与测量记录
            _startPointMs = null;
            _endPointMs = null;
            UpdateTimePointLabels();
            ClearMeasurements(silent: true);
            _currentVideoPath = filePath;

            if (_player.Open(filePath))
            {
                _fileOpened = true;
                _lblFileInfo.Text = $"文件: {Path.GetFileName(filePath)}  |  时长: {TimeFormatter.Format(_player.DurationMs)}" +
                                    (_player.Fps > 0 ? $"  |  {_player.Fps:F2} fps" : "");
                _btnPlay.Enabled = true;
                _btnPause.Enabled = true;
                _btnStop.Enabled = true;
                _btnSetStart.Enabled = true;
                _btnSetEnd.Enabled = true;
                _btnPrevFrame.Enabled = true;
                _btnNextFrame.Enabled = true;
                _btnEditStart.Enabled = true;
                _btnEditEnd.Enabled = true;
                _btnSnapshot.Enabled = true;
                _btnJumpStart.Enabled = true;
                _btnJumpEnd.Enabled = true;
                _chkLoopRegion.Enabled = true;
                _timeline.Enabled = true;

                // 时间轴最大值对齐时长（毫秒）
                _timeline.Maximum = Math.Max(1, _player.DurationMs);
                _timeline.Value = 0;
                _timeline.StartMarker = null;
                _timeline.EndMarker = null;

                // 上一次拖动的位置在新文件里不成立
                _dragPreviewMs = -1;

                _lblDuration.Text = TimeFormatter.Format(_player.DurationMs);

                // 应用当前速率与音量
                ApplyVolumeFromTrack();
            }
            else
            {
                _fileOpened = false;
                MessageBox.Show("打开视频文件失败，请查看错误信息。", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开文件异常: {ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            this.Cursor = Cursors.Default;
        }
    }

    #endregion

    #region 快捷键

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        // Esc 退出全屏
        if (e.KeyCode == Keys.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (!_fileOpened) return;

        switch (e.KeyCode)
        {
            case Keys.Space:
                if (_player.IsPlaying) _player.Pause();
                else _player.Play();
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;

            case Keys.Right:
                _player.StepFrame(1);
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;

            case Keys.Left:
                _player.StepFrame(-1);
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;

            case Keys.I:
                SetStartPoint(_player.CurrentPositionMs);
                e.Handled = true;
                break;

            case Keys.O:
                SetEndPoint(_player.CurrentPositionMs);
                e.Handled = true;
                break;

            case Keys.G:
                JumpToPoint(_startPointMs);
                e.Handled = true;
                break;

            case Keys.H:
                JumpToPoint(_endPointMs);
                e.Handled = true;
                break;

            case Keys.D:
                SaveSnapshot();
                e.Handled = true;
                break;

            case Keys.F:
                ToggleFullscreen();
                e.Handled = true;
                break;
        }
    }

    private void MainForm_MouseWheel(object? sender, MouseEventArgs e)
    {
        if (!_fileOpened) return;
        int step = e.Delta > 0 ? 5 : -5;
        int nv = Math.Clamp(_trkVolume.Value + step, _trkVolume.Minimum, _trkVolume.Maximum);
        _trkVolume.Value = nv;
        ApplyVolumeFromTrack();
    }

    #endregion

    #region 播放器事件

    private void OnFrameDecoded(Bitmap frame, long sequence)
    {
        if (this.IsDisposed)
        {
            frame.Dispose();
            return;
        }

        if (this.InvokeRequired)
        {
            this.BeginInvoke(new Action<Bitmap, long>(OnFrameDecoded), frame, sequence);
            return;
        }

        // 排队期间解出来的旧帧：直接丢，画面只认最新那张，
        // 否则拖动时界面会把已经过时的中间帧挨个补播一遍，看着就是慢半拍
        if (sequence < Volatile.Read(ref _displayedFrameSeq))
        {
            frame.Dispose();
            return;
        }
        Volatile.Write(ref _displayedFrameSeq, sequence);

        Bitmap? oldFrame;
        lock (_frameLock)
        {
            oldFrame = _currentFrame;
            _currentFrame = frame;
        }

        _videoPictureBox.Image = frame;
        oldFrame?.Dispose();
    }

    private void OnPositionChanged(long positionMs)
    {
        if (this.IsDisposed) return;

        if (this.InvokeRequired)
        {
            this.BeginInvoke(new Action<long>(OnPositionChanged), positionMs);
            return;
        }

        // 循环区间：播放到终点时跳回起点
        if (_chkLoopRegion.Checked && _player.IsPlaying &&
            _startPointMs.HasValue && _endPointMs.HasValue)
        {
            long lo = Math.Min(_startPointMs.Value, _endPointMs.Value);
            long hi = Math.Max(_startPointMs.Value, _endPointMs.Value);
            if (hi > lo && positionMs >= hi)
            {
                _player.Seek(lo);
                return; // 由新的位置事件刷新界面
            }
        }

        // 拖动期间界面由手指驱动：后台预览回调的位置可能已经落后好几个鼠标位置，
        // 让它写进来会把播放头和时间戳往回拽，看起来就是一顿一顿的
        if (_isDraggingTimeline) return;

        _lblCurrentTime.Text = TimeFormatter.Format(positionMs);
        _timeline.Value = positionMs;
    }

    private void OnDurationChanged(long durationMs)
    {
        if (this.InvokeRequired)
        {
            this.BeginInvoke(new Action<long>(OnDurationChanged), durationMs);
            return;
        }
        _lblDuration.Text = TimeFormatter.Format(durationMs);
    }

    private void OnPlayStateChanged(bool isPlaying)
    {
        if (this.InvokeRequired)
        {
            this.BeginInvoke(new Action<bool>(OnPlayStateChanged), isPlaying);
            return;
        }
        _btnPlay.Enabled = !isPlaying && _fileOpened;
        _btnPause.Enabled = isPlaying;
    }

    private void OnPlaybackEnded()
    {
        if (this.InvokeRequired)
        {
            this.BeginInvoke(new Action(OnPlaybackEnded));
            return;
        }
        _btnPlay.Enabled = true;
        _btnPause.Enabled = false;
    }

    private void OnPlayerError(string message)
    {
        if (this.InvokeRequired)
        {
            this.BeginInvoke(new Action<string>(OnPlayerError), message);
            return;
        }
        MessageBox.Show(message, "播放器错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    #endregion

    #region 时间轴拖动（画面实时跟随 + 起终点标记）

    private void Timeline_DragStarted()
    {
        _isDraggingTimeline = true;
        _dragSession++;
        _lastPreviewRequestTick = 0;
        _wasPlayingBeforeDrag = _player.IsPlaying;
        if (_wasPlayingBeforeDrag)
        {
            _player.Pause();
        }
    }

    private void Timeline_SeekPreview(long ms)
    {
        // 文字立即跟手；画面交给后台预览线程，UI 线程一帧都不等
        _dragPreviewMs = ms;
        _lblCurrentTime.Text = TimeFormatter.Format(ms);
        RequestPreview(ms);
    }

    /// <summary>
    /// 向预览线程提目标。鼠标移动比解码快得多，限速后既省 CPU，
    /// 也避免解码帧在 UI 消息队列里堆成一串大图。
    /// </summary>
    private void RequestPreview(long ms)
    {
        long now = Environment.TickCount64;
        if (now - _lastPreviewRequestTick < PreviewMinIntervalMs) return;
        _lastPreviewRequestTick = now;
        _player.BeginSeekPreview(ms);
    }

    private void Timeline_SeekCompleted()
    {
        long target = _dragPreviewMs;
        int session = _dragSession;
        _dragPreviewMs = -1;

        if (target >= 0)
        {
            // 最后一次移动可能被限速挡掉了，这里补提一次，保证停在手指位置。
            // 要恢复播放时走同步定位：seek + 清缓冲 + 复位时钟都很便宜（预览解码已按包让锁），
            // 主时钟在解除暂停前就落在目标位置，不会先把时间拽回拖前那点再跳回来；
            // 只是暂停着看画面时不用等，交给预览线程把这一帧解出来
            if (_wasPlayingBeforeDrag)
                _player.Seek(target);
            else
                _player.BeginSeekPreview(target);
        }

        // 位置以手指为准，不在 UI 线程等后台解码：松手时睡上几百毫秒正是「一卡一卡」的来源
        long settledMs = target >= 0 ? target : _player.CurrentPositionMs;

        if (this.IsDisposed || !this.IsHandleCreated)
        {
            _isDraggingTimeline = false;
        }
        else
        {
            // 抑制标志留到这一轮消息走完再清：拖动期间排进队列的位置回调如果晚于
            // 这里落地，会把播放头从刚定下的位置往回拽一下
            this.BeginInvoke(new Action(() =>
            {
                if (_dragSession != session) return; // 已经开始了下一次拖动，由它自己收尾
                _isDraggingTimeline = false;
                _lblCurrentTime.Text = TimeFormatter.Format(settledMs);
                _timeline.Value = settledMs;
            }));
        }

        if (_wasPlayingBeforeDrag)
        {
            _player.Play();
        }
        _wasPlayingBeforeDrag = false;
    }

    #endregion

    #region 跳转

    private void JumpToPoint(long? ms)
    {
        if (!_fileOpened || !ms.HasValue) return;
        _lblCurrentTime.Text = TimeFormatter.Format(ms.Value);
        _player.BeginSeekPreview(ms.Value);
    }

    private void JumpToMeasurementStart()
    {
        if (_lvHistory.SelectedItems.Count == 0) return;
        if (_lvHistory.SelectedItems[0].Tag is long[] range)
        {
            JumpToPoint(range[0]);
        }
    }

    #endregion

    #region 时间点设置与编辑

    private void SetStartPoint(long ms)
    {
        _startPointMs = ms;
        UpdateTimePointLabels();
    }

    private void SetEndPoint(long ms)
    {
        _endPointMs = ms;
        UpdateTimePointLabels();
    }

    private void EditStartPoint()
    {
        if (!_fileOpened) return;

        long initial = _startPointMs ?? _player.CurrentPositionMs;
        using var dlg = new TimePointEditor("编辑起点时间", initial,
            _player.FrameDurationMs, _player.DurationMs);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _startPointMs = dlg.ResultValue;
            UpdateTimePointLabels();
        }
    }

    private void EditEndPoint()
    {
        if (!_fileOpened) return;

        long initial = _endPointMs ?? _player.CurrentPositionMs;
        using var dlg = new TimePointEditor("编辑终点时间", initial,
            _player.FrameDurationMs, _player.DurationMs);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _endPointMs = dlg.ResultValue;
            UpdateTimePointLabels();
        }
    }

    private void UpdateTimePointLabels()
    {
        _lblStartTime.Text = _startPointMs.HasValue
            ? $"起点: {TimeFormatter.Format(_startPointMs.Value)}"
            : "起点: --:--:--.---";

        _lblEndTime.Text = _endPointMs.HasValue
            ? $"终点: {TimeFormatter.Format(_endPointMs.Value)}"
            : "终点: --:--:--.---";

        _timeline.StartMarker = _startPointMs;
        _timeline.EndMarker = _endPointMs;

        if (_startPointMs.HasValue && _endPointMs.HasValue)
        {
            long diff = Math.Abs(_endPointMs.Value - _startPointMs.Value);
            _lblSegmentDuration.Text = $"耗时: {TimeFormatter.Format(diff)}";
        }
        else
        {
            _lblSegmentDuration.Text = "耗时: --:--:--.---";
        }
    }

    #endregion

    #region 测量记录

    private void AddMeasurement()
    {
        if (!_startPointMs.HasValue || !_endPointMs.HasValue)
        {
            MessageBox.Show("请先设置起点和终点，再记录本段。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        long a = _startPointMs.Value;
        long b = _endPointMs.Value;
        long dur = Math.Abs(b - a);

        var item = new ListViewItem((_lvHistory.Items.Count + 1).ToString())
        {
            Tag = new[] { a, b }
        };
        item.SubItems.Add(TimeFormatter.Format(a));
        item.SubItems.Add(TimeFormatter.Format(b));
        item.SubItems.Add(TimeFormatter.Format(dur));

        _lvHistory.Items.Add(item);
        item.EnsureVisible();
        UpdateHistoryTitle();
    }

    private void DeleteSelectedMeasurements()
    {
        if (_lvHistory.SelectedItems.Count == 0) return;
        foreach (ListViewItem it in _lvHistory.SelectedItems)
        {
            _lvHistory.Items.Remove(it);
        }
        RenumberMeasurements();
        UpdateHistoryTitle();
    }

    private void ClearMeasurements(bool silent = false)
    {
        if (!silent && _lvHistory.Items.Count > 0)
        {
            var r = MessageBox.Show("确定清空所有测量记录？", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
        }
        _lvHistory.Items.Clear();
        UpdateHistoryTitle();
    }

    private void RenumberMeasurements()
    {
        for (int i = 0; i < _lvHistory.Items.Count; i++)
        {
            _lvHistory.Items[i].Text = (i + 1).ToString();
        }
    }

    private void UpdateHistoryTitle()
    {
        _lblHistoryTitle.Text = $"  测量记录（{_lvHistory.Items.Count}）";
    }

    /// <summary>折叠或展开右侧测量记录面板；折叠后仅保留一条带展开按钮的窄边。</summary>
    private void SetHistoryCollapsed(bool collapsed)
    {
        _historyCollapsed = collapsed;

        _lvHistory.Visible = !collapsed;
        _historyButtons.Visible = !collapsed;
        _lblHistoryTitle.Visible = !collapsed;
        _btnToggleHistory.Text = collapsed ? "▶" : "◀";

        _historyPanel.Width = collapsed ? HistoryCollapsedWidth : HistoryExpandedWidth;
    }

    /// <summary>剪贴板文本：制表符分隔，粘贴到 Excel / 飞书表格即可分列。</summary>
    private string BuildMeasurementsText()
    {
        var sb = new StringBuilder();
        string file = _currentVideoPath != null ? Path.GetFileName(_currentVideoPath) : "";
        sb.AppendLine($"视频: {file}");
        sb.AppendLine("序号\t起点\t终点\t耗时");
        foreach (ListViewItem it in _lvHistory.Items)
        {
            sb.AppendLine($"{it.SubItems[0].Text}\t{it.SubItems[1].Text}\t{it.SubItems[2].Text}\t{it.SubItems[3].Text}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 导出文件用的 CSV：逗号分隔并按 RFC4180 转义。
    /// 制表符分隔的内容存成 .csv 时 Excel 不会分列，整份记录会挤在一列里。
    /// </summary>
    private string BuildCsvText()
    {
        var sb = new StringBuilder();
        string file = _currentVideoPath != null ? Path.GetFileName(_currentVideoPath) : "";
        sb.AppendLine(CsvField($"视频: {file}"));
        sb.AppendLine("序号,起点,终点,耗时");
        foreach (ListViewItem it in _lvHistory.Items)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                CsvField(it.SubItems[0].Text),
                CsvField(it.SubItems[1].Text),
                CsvField(it.SubItems[2].Text),
                CsvField(it.SubItems[3].Text)
            }));
        }
        return sb.ToString();
    }

    /// <summary>含逗号、引号或换行时用双引号包裹字段，内部引号翻倍。</summary>
    private static string CsvField(string value)
    {
        if (value.IndexOf(',') < 0 && value.IndexOf('"') < 0 &&
            value.IndexOf('\r') < 0 && value.IndexOf('\n') < 0)
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private void CopyMeasurements()
    {
        if (_lvHistory.Items.Count == 0)
        {
            MessageBox.Show("暂无测量记录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Clipboard.SetText(BuildMeasurementsText());
        MessageBox.Show("已复制到剪贴板。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExportMeasurements()
    {
        if (_lvHistory.Items.Count == 0)
        {
            MessageBox.Show("暂无测量记录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string baseName = _currentVideoPath != null
            ? Path.GetFileNameWithoutExtension(_currentVideoPath)
            : "measurements";

        using var sfd = new SaveFileDialog
        {
            Title = "导出测量记录",
            Filter = "CSV 文件|*.csv|文本文件|*.txt",
            FileName = $"{baseName}_测量记录.csv"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(sfd.FileName, BuildCsvText(), new UTF8Encoding(true));
            MessageBox.Show($"已导出：{sfd.FileName}", "导出成功",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败: {ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    #endregion

    #region 截图

    private void SaveSnapshot()
    {
        if (!_fileOpened) return;

        Bitmap? frame;
        lock (_frameLock) { frame = _currentFrame; }
        if (frame == null)
        {
            MessageBox.Show("当前没有可保存的画面。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        long ms = _player.CurrentPositionMs;
        string baseName = _currentVideoPath != null
            ? Path.GetFileNameWithoutExtension(_currentVideoPath)
            : "video";
        string defName = $"{baseName}_{TimeFormatter.FormatForFileName(ms)}.png";

        using var sfd = new SaveFileDialog
        {
            Title = "保存当前画面",
            Filter = "PNG 图片|*.png",
            FileName = defName
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            lock (_frameLock)
            {
                _currentFrame?.Save(sfd.FileName, ImageFormat.Png);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    #endregion

    #region 播放体验（倍速 / 音量 / 全屏）

    private void CboSpeed_SelectedIndexChanged(object? sender, EventArgs e)
    {
        string text = _cboSpeed.SelectedItem?.ToString() ?? "1x";
        string num = text.TrimEnd('x', 'X');
        if (double.TryParse(num, out double rate))
        {
            _player.SetPlaybackRate(rate);
        }
    }

    private void ApplyVolumeFromTrack()
    {
        _volume = _trkVolume.Value / 100.0;
        _player.SetVolume(_volume);
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _prevBorderStyle = this.FormBorderStyle;
            _prevWindowState = this.WindowState;
            _prevBounds = this.Bounds;

            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;

            _isFullscreen = true;
            _btnFullscreen.Text = "退出";
        }
        else
        {
            this.FormBorderStyle = _prevBorderStyle;
            this.WindowState = _prevWindowState;
            this.Bounds = _prevBounds;

            _isFullscreen = false;
            _btnFullscreen.Text = "全屏";
        }
    }

    #endregion

    #region 更新检查

    private async void BtnUpdate_Click(object? sender, EventArgs e)
    {
        if (_updateInProgress) return;
        _updateInProgress = true;
        string oldText = _btnUpdate.Text;
        _btnUpdate.Enabled = false;
        try
        {
            _btnUpdate.Text = "检查中…";
            Updater.ReleaseInfo? latest;
            try
            {
                latest = await Updater.GetLatestReleaseAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检查更新失败：{ex.Message}\n\n请检查网络连接。", "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (latest == null)
            {
                MessageBox.Show("未能获取最新版本信息，或最新版本没有可用的安装包。", "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Version current = GetCurrentVersion();
            if (latest.Version <= current)
            {
                MessageBox.Show($"当前已是最新版本（v{current}）。", "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            long mb = Math.Max(1, latest.AssetSize / 1024 / 1024);
            var r = MessageBox.Show(
                $"发现新版本 {latest.Tag}（当前 v{current}）。\n是否下载更新？约 {mb} MB。",
                "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            // 下载到临时目录
            string tempDir = Path.Combine(Path.GetTempPath(), "VideoCountMilli_update");
            try { Directory.Delete(tempDir, true); } catch { /* 清理旧目录失败可忽略 */ }
            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, "update.zip");

            _btnUpdate.Text = "下载中 0%";
            var progress = new Progress<double>(p => _btnUpdate.Text = $"下载中 {p:F0}%");
            await Updater.DownloadFileAsync(latest.AssetUrl, zipPath, progress);

            // 完整性校验：大小必须与 Release 资产一致；GitHub 提供摘要时再比对 SHA256
            _btnUpdate.Text = "校验中…";
            long actualSize = new FileInfo(zipPath).Length;
            if (latest.AssetSize > 0 && actualSize != latest.AssetSize)
            {
                MessageBox.Show(
                    $"下载文件不完整（实际 {actualSize / 1024 / 1024} MB，应为 " +
                    $"{latest.AssetSize / 1024 / 1024} MB），已中止更新。",
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (latest.Sha256 != null)
            {
                string actualSha = await Updater.ComputeFileSha256Async(zipPath);
                if (!string.Equals(actualSha, latest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "下载文件校验失败（SHA256 与 GitHub 上发布的不一致），已中止更新。\n" +
                        "可能是下载中断或传输内容被改动，请重新尝试。",
                        "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            _btnUpdate.Text = "解压中…";
            string extractDir = Path.Combine(tempDir, "extract");
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // zip 内含单个顶层文件夹时进入该文件夹
            string sourceDir = extractDir;
            string[] subDirs = Directory.GetDirectories(extractDir);
            if (subDirs.Length == 1 && Directory.GetFiles(extractDir).Length == 0)
            {
                sourceDir = subDirs[0];
            }

            string appDir = AppContext.BaseDirectory;
            if (!IsDirectoryWritable(appDir))
            {
                MessageBox.Show($"程序目录不可写，无法自动安装。\n更新包已下载到：\n{zipPath}\n请手动解压并覆盖程序目录。",
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 安装脚本必须放在 tempDir 之外：正在被 PowerShell 执行的脚本文件
            // 无法可靠删除，若它位于待清理的 tempDir 内，清理失败会连带跳过重启
            string ps1Path = Path.Combine(Path.GetTempPath(), "VideoCountMilli_update_install.ps1");
            string exePath = Path.Combine(appDir, "VideoCountMilli.exe");
            string script = Updater.BuildInstallScript(sourceDir, appDir, tempDir, exePath);
            File.WriteAllText(ps1Path, script, new UTF8Encoding(true));

            // 安装会重启程序，而未导出的测量记录不会跨会话保留，重启前先自动备份
            string backupNote = "";
            if (_lvHistory.Items.Count > 0)
            {
                string? backupPath = BackupMeasurementsBeforeUpdate();
                backupNote = backupPath != null
                    ? $"\n\n{_lvHistory.Items.Count} 条测量记录已自动备份到：\n{backupPath}"
                    : "\n\n注意：测量记录自动备份失败，建议先点「导出CSV」再继续更新。";
            }

            var r2 = MessageBox.Show(
                "更新包已下载、校验并解压完成。" + backupNote +
                "\n\n点击“是”将关闭程序并自动安装更新（完成后程序会自动重启）。\n" +
                "点击“否”可稍后安装：关闭程序后运行\n" + ps1Path,
                "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (r2 == DialogResult.Yes)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1Path}\" {Environment.ProcessId}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                System.Diagnostics.Process.Start(psi);
                Application.Exit();
            }
            else
            {
                MessageBox.Show($"已取消自动安装。可关闭程序后运行：\n{ps1Path}", "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"更新失败：{ex.Message}", "检查更新",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnUpdate.Text = oldText;
            _btnUpdate.Enabled = true;
            _updateInProgress = false;
        }
    }

    private static Version GetCurrentVersion()
    {
        string raw = Application.ProductVersion.Split('+')[0];
        return Version.TryParse(raw, out var v) ? v : new Version(0, 0, 0);
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".write_test_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "t");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 安装更新前把当前测量记录自动备份到「文档\VideoCountMilli_测量备份」。
    /// 记录列表不落盘、重启即丢失，而自动安装会直接结束本进程，
    /// 因此不能指望用户记得先手动导出。无记录或备份失败时返回 null。
    /// </summary>
    private string? BackupMeasurementsBeforeUpdate()
    {
        if (_lvHistory.Items.Count == 0) return null;

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "VideoCountMilli_测量备份");
            Directory.CreateDirectory(dir);

            string baseName = _currentVideoPath != null
                ? Path.GetFileNameWithoutExtension(_currentVideoPath)
                : "measurements";
            string path = Path.Combine(dir,
                $"{baseName}_测量记录_{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            File.WriteAllText(path, BuildCsvText(), new UTF8Encoding(true));
            return path;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region 背景设置

    private void BtnBackground_Click(object? sender, EventArgs e)
    {
        using var dlg = new BackgroundSettingsDialog(
            _videoPictureBox.BackColor, _videoPictureBox.BackgroundImage);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        ApplyBackground(dlg.SelectedColor, dlg.SelectedImage, save: true);
    }

    private void ApplyBackground(Color color, Image? image, bool save)
    {
        var oldImage = _videoPictureBox.BackgroundImage;
        _videoPictureBox.BackColor = color;
        _videoPictureBox.BackgroundImage = image;
        _videoPictureBox.BackgroundImageLayout = image != null ? ImageLayout.Zoom : ImageLayout.None;

        if (!ReferenceEquals(oldImage, image))
        {
            oldImage?.Dispose();
        }

        if (save)
        {
            SaveSettings();
        }
    }

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VideoCountMilli", "settings.json");

    private void LoadSettings()
    {
        try
        {
            string path = SettingsFilePath;
            if (!File.Exists(path)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            int mode = doc.RootElement.GetProperty("mode").GetInt32();
            if (mode == 1)
            {
                string imgPath = doc.RootElement.GetProperty("image").GetString() ?? "";
                if (imgPath.Length > 0 && File.Exists(imgPath))
                {
                    using var tmp = Image.FromFile(imgPath);
                    _backgroundImagePath = imgPath;
                    ApplyBackground(Theme.Window, new Bitmap(tmp), save: false);
                }
            }
            else
            {
                int argb = doc.RootElement.GetProperty("argb").GetInt32();
                ApplyBackground(Color.FromArgb(argb), null, save: false);
            }
        }
        catch
        {
            // 设置文件损坏时忽略，使用默认背景
        }
    }

    private void SaveSettings()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsFilePath);
            if (dir != null) Directory.CreateDirectory(dir);

            string json = _videoPictureBox.BackgroundImage != null
                ? JsonSerializer.Serialize(new { mode = 1, argb = 0, image = _backgroundImagePath ?? "" })
                : JsonSerializer.Serialize(new { mode = 0, argb = _videoPictureBox.BackColor.ToArgb(), image = "" });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // 保存失败时忽略
        }
    }

    #endregion

    #region 清理

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _player.Dispose();
        lock (_frameLock)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
        }
    }

    #endregion
}
