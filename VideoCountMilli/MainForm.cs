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
                _progressBar.Value = 0;
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

    #region 进度条拖动

    private void ProgressBar_MouseDown(object? sender, MouseEventArgs e)
    {
        _isDraggingProgress = true;
    }

    private void ProgressBar_MouseUp(object? sender, MouseEventArgs e)
    {
        if (_isDraggingProgress)
        {
            SeekToProgressValue();
            _isDraggingProgress = false;
        }
    }

    private void ProgressBar_Scroll(object? sender, EventArgs e)
    {
        if (!_isDraggingProgress)
        {
            // 键盘操作时直接跳转
            SeekToProgressValue();
        }
    }

    private void SeekToProgressValue()
    {
        if (_player.DurationMs <= 0) return;

        double ratio = (double)_progressBar.Value / _progressBar.Maximum;
        long targetMs = (long)(ratio * _player.DurationMs);
        _player.Seek(targetMs);
        _lblCurrentTime.Text = TimeFormatter.Format(targetMs);
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
        _player.Dispose();
        lock (_frameLock)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
        }
    }

    #endregion
}
