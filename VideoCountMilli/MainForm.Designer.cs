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
    private Button _btnPrevFrame;
    private Button _btnNextFrame;
    private Button _btnSetStart;
    private Button _btnSetEnd;
    private Button _btnEditStart;
    private Button _btnEditEnd;
    private Label _lblStartTime;
    private Label _lblEndTime;
    private Label _lblSegmentDuration;
    private Label _lblFileInfo;
    private Label _lblShortcuts;

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
        this.ClientSize = new Size(980, 690);
        this.Text = "视频毫秒计时器 - VideoCountMilli";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MinimumSize = new Size(700, 520);
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
            Height = 185,
            BackColor = Color.FromArgb(40, 40, 40)
        };

        // 第 1 行：文件信息
        _lblFileInfo = new Label
        {
            Text = "未打开文件",
            ForeColor = Color.LightGray,
            AutoSize = false,
            Location = new Point(12, 8),
            Size = new Size(950, 18),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9F)
        };

        // 第 2 行：当前时间 / 总时长 + 快捷键提示
        _lblCurrentTime = new Label
        {
            Text = "00:00:00.000",
            ForeColor = Color.Lime,
            Font = new Font("Consolas", 14F, FontStyle.Bold),
            Location = new Point(12, 32),
            AutoSize = true
        };

        var lblSep = new Label
        {
            Text = "/",
            ForeColor = Color.Gray,
            Font = new Font("Consolas", 14F),
            Location = new Point(185, 32),
            AutoSize = true
        };

        _lblDuration = new Label
        {
            Text = "00:00:00.000",
            ForeColor = Color.LightGray,
            Font = new Font("Consolas", 14F),
            Location = new Point(205, 32),
            AutoSize = true
        };

        _lblShortcuts = new Label
        {
            Text = "快捷键: 空格 播放/暂停 · ←/→ 逐帧 · I 设起点 · O 设终点",
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 8.5F),
            Location = new Point(470, 40),
            AutoSize = true
        };

        // 第 3 行：进度条
        _progressBar = new TrackBar
        {
            Location = new Point(12, 62),
            Size = new Size(950, 42),
            Minimum = 0,
            Maximum = 1000,
            TickStyle = TickStyle.None,
            Enabled = false
        };

        // 第 4 行：播放控制 + 逐帧按钮
        int btnY = 112;
        _btnOpen = new Button { Text = "打开", Location = new Point(12, btnY), Size = new Size(70, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        _btnPlay = new Button { Text = "播放", Location = new Point(86, btnY), Size = new Size(70, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnPause = new Button { Text = "暂停", Location = new Point(160, btnY), Size = new Size(70, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnStop = new Button { Text = "停止", Location = new Point(234, btnY), Size = new Size(70, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnPrevFrame = new Button { Text = "上一帧", Location = new Point(314, btnY), Size = new Size(75, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnNextFrame = new Button { Text = "下一帧", Location = new Point(393, btnY), Size = new Size(75, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };

        // 第 5 行：时间点设置/编辑 + 耗时
        int ptY = 148;
        _btnSetStart = new Button { Text = "设为起点(I)", Location = new Point(12, ptY), Size = new Size(95, 26), BackColor = Color.FromArgb(0, 120, 215), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _lblStartTime = new Label
        {
            Text = "起点: --:--:--.---",
            ForeColor = Color.DeepSkyBlue,
            Font = new Font("Consolas", 10F),
            Location = new Point(112, ptY + 3),
            Size = new Size(170, 18),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        _btnEditStart = new Button { Text = "编辑", Location = new Point(285, ptY), Size = new Size(42, 26), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5F), Enabled = false };

        _btnSetEnd = new Button { Text = "设为终点(O)", Location = new Point(337, ptY), Size = new Size(95, 26), BackColor = Color.FromArgb(200, 80, 0), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _lblEndTime = new Label
        {
            Text = "终点: --:--:--.---",
            ForeColor = Color.Orange,
            Font = new Font("Consolas", 10F),
            Location = new Point(437, ptY + 3),
            Size = new Size(170, 18),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        _btnEditEnd = new Button { Text = "编辑", Location = new Point(610, ptY), Size = new Size(42, 26), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5F), Enabled = false };

        _lblSegmentDuration = new Label
        {
            Text = "耗时: --:--:--.---",
            ForeColor = Color.Lime,
            Font = new Font("Consolas", 11F, FontStyle.Bold),
            Location = new Point(668, ptY + 1),
            AutoSize = true
        };

        // 添加控件
        _controlPanel.Controls.AddRange(new Control[]
        {
            _lblFileInfo, _lblCurrentTime, lblSep, _lblDuration, _lblShortcuts,
            _progressBar,
            _btnOpen, _btnPlay, _btnPause, _btnStop, _btnPrevFrame, _btnNextFrame,
            _btnSetStart, _lblStartTime, _btnEditStart,
            _btnSetEnd, _lblEndTime, _btnEditEnd,
            _lblSegmentDuration
        });

        this.Controls.Add(_videoPictureBox);
        this.Controls.Add(_controlPanel);

        this.ResumeLayout(false);
        this.PerformLayout();
    }
}
