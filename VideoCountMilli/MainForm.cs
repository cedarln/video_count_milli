using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VideoCountMilli;

public partial class MainForm : Form
{
    private readonly FFmpegPlayer _player;
    private long? _startPointMs;
    private long? _endPointMs;
    private bool _isDraggingProgress;
    private bool _fileOpened;
    private Bitmap? _currentFrame;
    private readonly object _frameLock = new();

    // 拖动进度条时的实时画面预览（节流，避免频繁 seek 造成卡顿）
    private bool _wasPlayingBeforeDrag;
    private readonly System.Windows.Forms.Timer _previewTimer = new();
    private long _pendingPreviewMs = -1;
    private long _lastPreviewMs = -1;

    public MainForm()
    {
        InitializeComponent();
        _player = new FFmpegPlayer();

        // 绑定事件
        _player.FrameDecoded += OnFrameDecoded;
        _player.PositionChanged += OnPositionChanged;
        _player.DurationChanged += OnDurationChanged;
        _player.PlayStateChanged += OnPlayStateChanged;
        _player.PlaybackEnded += OnPlaybackEnded;
        _player.Error += OnPlayerError;

        // 拖动进度条时的画面预览节流定时器（40ms 执行一次 seek+解码）
        _previewTimer.Interval = 40;
        _previewTimer.Tick += PreviewTimer_Tick;

        // 按钮事件
        _btnOpen.Click += BtnOpen_Click;
        _btnPlay.Click += (s, e) => _player.Play();
        _btnPause.Click += (s, e) => _player.Pause();
        _btnStop.Click += (s, e) => _player.Stop();
        _btnSetStart.Click += (s, e) => SetStartPoint(_player.CurrentPositionMs);
        _btnSetEnd.Click += (s, e) => SetEndPoint(_player.CurrentPositionMs);
        _btnPrevFrame.Click += (s, e) => _player.StepFrame(-1);
        _btnNextFrame.Click += (s, e) => _player.StepFrame(1);

        // 时间点编辑（按钮或点击时间文字）
        _btnEditStart.Click += (s, e) => EditStartPoint();
        _btnEditEnd.Click += (s, e) => EditEndPoint();
        _lblStartTime.Click += (s, e) => EditStartPoint();
        _lblEndTime.Click += (s, e) => EditEndPoint();

        // 进度条事件
        _progressBar.MouseDown += ProgressBar_MouseDown;
        _progressBar.MouseUp += ProgressBar_MouseUp;
        _progressBar.Scroll += ProgressBar_Scroll;

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

            // 重置时间点
            _startPointMs = null;
            _endPointMs = null;
            UpdateTimePointLabels();

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
                _progressBar.Enabled = true;
                // 进度条最大值设为时长（毫秒），使拖动精度直接对应毫秒
                int maxVal = (int)Math.Min(int.MaxValue, Math.Max(1, _player.DurationMs));
                _progressBar.LargeChange = 1;
                _progressBar.SmallChange = 1;
                _progressBar.Maximum = maxVal;
                _progressBar.LargeChange = Math.Max(1, maxVal / 200);
                _progressBar.Value = 0;
                _lastPreviewMs = -1;
                _pendingPreviewMs = -1;
                _previewTimer.Stop();
                _lblDuration.Text = TimeFormatter.Format(_player.DurationMs);
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
        if (!_fileOpened) return;

        switch (e.KeyCode)
        {
            case Keys.Space:
                if (_player.IsPlaying)
                {
                    _player.Pause();
                }
                else
                {
                    _player.Play();
                }
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
        }
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

        // 在 UI 线程更新画面
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

        _lblCurrentTime.Text = TimeFormatter.Format(positionMs);

        if (!_isDraggingProgress && _player.DurationMs > 0)
        {
            double ratio = (double)positionMs / _player.DurationMs;
            int value = (int)Math.Clamp(ratio * _progressBar.Maximum, 0, _progressBar.Maximum);
            _progressBar.Value = value;
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

    #region 进度条拖动（拖动时画面实时跟随）

    private void ProgressBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_player.DurationMs <= 0) return;

        _isDraggingProgress = true;
        // 拖动时暂停播放，避免解码线程与预览 seek 冲突；记住原状态以便松开后恢复
        _wasPlayingBeforeDrag = _player.IsPlaying;
        if (_wasPlayingBeforeDrag)
        {
            _player.Pause();
        }

        // 立即在按下位置显示一帧
        BeginPreview(_progressBar.Value);
    }

    private void ProgressBar_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!_isDraggingProgress) return;

        _isDraggingProgress = false;
        _previewTimer.Stop();

        // 松开后在最终位置做一次精确预览，确保画面与进度条一致
        PreviewSeek(TrackValueToMs(_progressBar.Value));

        // 若拖动前正在播放，则从新位置继续播放
        if (_wasPlayingBeforeDrag)
        {
            _player.Play();
        }
        _wasPlayingBeforeDrag = false;
    }

    private void ProgressBar_Scroll(object? sender, EventArgs e)
    {
        if (_isDraggingProgress)
        {
            // 拖动过程中：请求预览（由定时器节流执行）
            BeginPreview(_progressBar.Value);
        }
        else
        {
            // 键盘操作进度条：立即跳转并预览画面
            PreviewSeek(TrackValueToMs(_progressBar.Value));
        }
    }

    /// <summary>标记待预览位置并启动节流定时器；立即更新时间文字，画面稍后刷新。</summary>
    private void BeginPreview(int trackValue)
    {
        _pendingPreviewMs = TrackValueToMs(trackValue);
        _lblCurrentTime.Text = TimeFormatter.Format(_pendingPreviewMs);
        if (!_previewTimer.Enabled)
        {
            _previewTimer.Start();
        }
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isDraggingProgress)
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
            // 画面已追上最新位置，暂停定时器以节省解码开销
            _previewTimer.Stop();
        }
    }

    /// <summary>跳转到指定时刻并解码显示该处画面帧（帧级精度）。</summary>
    private void PreviewSeek(long ms)
    {
        _lastPreviewMs = ms;
        _player.SeekPreview(ms);
    }

    /// <summary>进度条数值换算为毫秒位置。</summary>
    private long TrackValueToMs(int trackValue)
    {
        long dur = _player.DurationMs;
        if (dur <= 0 || _progressBar.Maximum <= 0) return 0;
        return (long)((double)trackValue / _progressBar.Maximum * dur);
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
        if (_startPointMs.HasValue)
        {
            _lblStartTime.Text = $"起点: {TimeFormatter.Format(_startPointMs.Value)}";
        }
        else
        {
            _lblStartTime.Text = "起点: --:--:--.---";
        }

        if (_endPointMs.HasValue)
        {
            _lblEndTime.Text = $"终点: {TimeFormatter.Format(_endPointMs.Value)}";
        }
        else
        {
            _lblEndTime.Text = "终点: --:--:--.---";
        }

        // 计算两点间耗时（取绝对差值，起点/终点顺序不限）
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
