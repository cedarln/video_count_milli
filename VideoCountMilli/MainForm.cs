using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
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

    // 拖动时间轴时的实时画面预览（节流，避免频繁 seek 造成卡顿）
    private bool _wasPlayingBeforeDrag;
    private readonly System.Windows.Forms.Timer _previewTimer = new();
    private long _pendingPreviewMs = -1;
    private long _lastPreviewMs = -1;

    // 音量 / 全屏
    private double _volume = 1.0;
    private bool _isFullscreen;
    private FormBorderStyle _prevBorderStyle;
    private FormWindowState _prevWindowState;
    private Rectangle _prevBounds;

    public MainForm()
    {
        InitializeComponent();
        _player = new FFmpegPlayer();

        // 绑定播放器事件
        _player.FrameDecoded += OnFrameDecoded;
        _player.PositionChanged += OnPositionChanged;
        _player.DurationChanged += OnDurationChanged;
        _player.PlayStateChanged += OnPlayStateChanged;
        _player.PlaybackEnded += OnPlaybackEnded;
        _player.Error += OnPlayerError;

        // 拖动时间轴时的画面预览节流定时器（40ms 执行一次 seek+解码）
        _previewTimer.Interval = 40;
        _previewTimer.Tick += PreviewTimer_Tick;

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
        _lvHistory.MouseDoubleClick += (s, e) => JumpToMeasurementStart();

        // 拖放文件支持
        this.AllowDrop = true;
        this.DragEnter += MainForm_DragEnter;
        this.DragDrop += MainForm_DragDrop;

        // 键盘快捷键
        this.KeyPreview = true;
        this.KeyDown += MainForm_KeyDown;

        this.FormClosing += MainForm_FormClosing;
    }

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

                _lastPreviewMs = -1;
                _pendingPreviewMs = -1;
                _previewTimer.Stop();

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

    private void OnFrameDecoded(Bitmap frame)
    {
        if (this.IsDisposed)
        {
            frame.Dispose();
            return;
        }

        if (this.InvokeRequired)
        {
            this.BeginInvoke(new Action<Bitmap>(OnFrameDecoded), frame);
            return;
        }

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

        _lblCurrentTime.Text = TimeFormatter.Format(positionMs);

        if (!_isDraggingTimeline)
        {
            _timeline.Value = positionMs;
        }
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
        _wasPlayingBeforeDrag = _player.IsPlaying;
        if (_wasPlayingBeforeDrag)
        {
            _player.Pause();
        }
    }

    private void Timeline_SeekPreview(long ms)
    {
        BeginPreview(ms);
    }

    private void Timeline_SeekCompleted()
    {
        _isDraggingTimeline = false;
        _previewTimer.Stop();

        if (_pendingPreviewMs >= 0)
        {
            PreviewSeek(_pendingPreviewMs);
        }

        if (_wasPlayingBeforeDrag)
        {
            _player.Play();
        }
        _wasPlayingBeforeDrag = false;
    }

    /// <summary>标记待预览位置并启动节流定时器；立即更新时间文字，画面稍后刷新。</summary>
    private void BeginPreview(long ms)
    {
        _pendingPreviewMs = ms;
        _lblCurrentTime.Text = TimeFormatter.Format(ms);
        if (!_previewTimer.Enabled)
        {
            _previewTimer.Start();
        }
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isDraggingTimeline)
        {
            _previewTimer.Stop();
            return;
        }

        if (_pendingPreviewMs != _lastPreviewMs)
        {
            PreviewSeek(_pendingPreviewMs);
        }
        else
        {
            _previewTimer.Stop();
        }
    }

    /// <summary>跳转到指定时刻并解码显示该处画面帧（帧级精度）。</summary>
    private void PreviewSeek(long ms)
    {
        _lastPreviewMs = ms;
        _player.SeekPreview(ms);
    }

    #endregion

    #region 跳转

    private void JumpToPoint(long? ms)
    {
        if (!_fileOpened || !ms.HasValue) return;
        PreviewSeek(ms.Value);
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
            File.WriteAllText(sfd.FileName, BuildMeasurementsText(), new UTF8Encoding(true));
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

    #region 清理

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _previewTimer.Stop();
        _previewTimer.Dispose();
        _player.Dispose();
        lock (_frameLock)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
        }
    }

    #endregion
}
