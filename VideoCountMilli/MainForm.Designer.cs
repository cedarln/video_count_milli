using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace VideoCountMilli;

partial class MainForm
{
    private IContainer components = null;

    private PictureBox _videoPictureBox;
    private Panel _controlPanel;
    private Label _lblCurrentTime;
    private Label _lblDuration;
    private TrackBar _progressBar;
    private Button _btnOpen;
    private Button _btnPlay;
    private Button _btnPause;
    private Button _btnStop;
    private Button _btnSetStart;
    private Button _btnSetEnd;
    private Label _lblStartTime;
    private Label _lblEndTime;
    private Label _lblSegmentDuration;
    private Label _lblFileInfo;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        this.SuspendLayout();

        // 窗体
        this.AutoScaleDimensions = new SizeF(7F, 15F);
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(960, 640);
        this.Text = "视频毫秒计时器 - VideoCountMilli";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MinimumSize = new Size(640, 480);
        this.BackColor = Color.Black;

        // 视频显示区
        _videoPictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black
        };

        // 控制面板
        _controlPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 140,
            BackColor = Color.FromArgb(40, 40, 40)
        };

        // 文件名
        _lblFileInfo = new Label
        {
            Text = "未打开文件",
            ForeColor = Color.LightGray,
            AutoSize = false,
            Location = new Point(12, 8),
            Size = new Size(920, 18),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9F)
        };

        // 当前时间
        _lblCurrentTime = new Label
        {
            Text = "00:00:00.000",
            ForeColor = Color.Lime,
            Font = new Font("Consolas", 14F, FontStyle.Bold),
            Location = new Point(12, 32),
            AutoSize = true
        };

        // 分隔符
        var lblSep = new Label
        {
            Text = "/",
            ForeColor = Color.Gray,
            Font = new Font("Consolas", 14F),
            Location = new Point(185, 32),
            AutoSize = true
        };

        // 总时长
        _lblDuration = new Label
        {
            Text = "00:00:00.000",
            ForeColor = Color.LightGray,
            Font = new Font("Consolas", 14F),
            Location = new Point(200, 32),
            AutoSize = true
        };

        // 进度条
        _progressBar = new TrackBar
        {
            Location = new Point(12, 62),
            Size = new Size(920, 45),
            Minimum = 0,
            Maximum = 1000,
            TickStyle = TickStyle.None,
            Enabled = false
        };

        // 按钮
        int btnY = 100;
        _btnOpen = new Button { Text = "打开", Location = new Point(12, btnY), Size = new Size(75, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        _btnPlay = new Button { Text = "播放", Location = new Point(97, btnY), Size = new Size(75, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnPause = new Button { Text = "暂停", Location = new Point(182, btnY), Size = new Size(75, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnStop = new Button { Text = "停止", Location = new Point(267, btnY), Size = new Size(75, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnSetStart = new Button { Text = "设为起点", Location = new Point(370, btnY), Size = new Size(90, 28), BackColor = Color.FromArgb(0, 120, 215), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnSetEnd = new Button { Text = "设为终点", Location = new Point(470, btnY), Size = new Size(90, 28), BackColor = Color.FromArgb(200, 80, 0), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };

        // 时间点信息
        _lblStartTime = new Label
        {
            Text = "起点: --:--:--.---",
            ForeColor = Color.DeepSkyBlue,
            Font = new Font("Consolas", 10F),
            Location = new Point(580, 100),
            AutoSize = true
        };

        _lblEndTime = new Label
        {
            Text = "终点: --:--:--.---",
            ForeColor = Color.Orange,
            Font = new Font("Consolas", 10F),
            Location = new Point(580, 120),
            AutoSize = true
        };

        _lblSegmentDuration = new Label
        {
            Text = "耗时: --:--:--.---",
            ForeColor = Color.Lime,
            Font = new Font("Consolas", 11F, FontStyle.Bold),
            Location = new Point(760, 108),
            AutoSize = true
        };

        // 添加控件
        _controlPanel.Controls.AddRange(new Control[]
        {
            _lblFileInfo, _lblCurrentTime, lblSep, _lblDuration,
            _progressBar,
            _btnOpen, _btnPlay, _btnPause, _btnStop, _btnSetStart, _btnSetEnd,
            _lblStartTime, _lblEndTime, _lblSegmentDuration
        });

        this.Controls.Add(_videoPictureBox);
        this.Controls.Add(_controlPanel);

        this.ResumeLayout(false);
        this.PerformLayout();
    }
}
