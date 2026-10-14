using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace VideoCountMilli;

partial class MainForm
{
    private IContainer components = null;

    // 视频显示
    private PictureBox _videoPictureBox;

    // 底部控制面板
    private Panel _controlPanel;
    private Label _lblFileInfo;
    private Label _lblCurrentTime;
    private Label _lblDuration;
    private Label _lblShortcuts;
    private TimelineBar _timeline;

    private Button _btnOpen;
    private Button _btnPlay;
    private Button _btnPause;
    private Button _btnStop;
    private Button _btnPrevFrame;
    private Button _btnNextFrame;
    private Button _btnSnapshot;
    private Button _btnFullscreen;
    private CheckBox _chkTopMost;
    private ComboBox _cboSpeed;
    private TrackBar _trkVolume;
    private CheckBox _chkMute;

    private Button _btnSetStart;
    private Button _btnSetEnd;
    private Button _btnEditStart;
    private Button _btnEditEnd;
    private Button _btnJumpStart;
    private Button _btnJumpEnd;
    private CheckBox _chkLoopRegion;
    private Label _lblStartTime;
    private Label _lblEndTime;
    private Label _lblSegmentDuration;

    // 右侧测量记录面板
    private Panel _historyPanel;
    private Panel _historyButtons;
    private Label _lblHistoryTitle;
    private ListView _lvHistory;
    private Button _btnAddMeasure;
    private Button _btnDelMeasure;
    private Button _btnClearMeasure;
    private Button _btnCopyMeasure;
    private Button _btnExportMeasure;

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
        this.ClientSize = new Size(1000, 720);
        this.Text = "视频毫秒计时器 - VideoCountMilli";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MinimumSize = new Size(760, 560);
        this.BackColor = Color.Black;

        // 视频显示区
        _videoPictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black
        };

        // ---------- 底部控制面板 ----------
        _controlPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 224,
            BackColor = Color.FromArgb(40, 40, 40)
        };

        // 第 1 行：文件信息
        _lblFileInfo = new Label
        {
            Text = "未打开文件",
            ForeColor = Color.LightGray,
            AutoSize = false,
            Location = new Point(12, 6),
            Size = new Size(970, 18),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9F),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        // 第 2 行：当前时间 / 总时长
        _lblCurrentTime = new Label
        {
            Text = "00:00:00.000",
            ForeColor = Color.Lime,
            Font = new Font("Consolas", 14F, FontStyle.Bold),
            Location = new Point(12, 28),
            AutoSize = true
        };

        var lblSep = new Label
        {
            Text = "/",
            ForeColor = Color.Gray,
            Font = new Font("Consolas", 14F),
            Location = new Point(185, 28),
            AutoSize = true
        };

        _lblDuration = new Label
        {
            Text = "00:00:00.000",
            ForeColor = Color.LightGray,
            Font = new Font("Consolas", 14F),
            Location = new Point(205, 28),
            AutoSize = true
        };

        // 第 3 行：时间轴
        _timeline = new TimelineBar
        {
            Location = new Point(12, 60),
            Size = new Size(970, 46),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Enabled = false
        };

        // 第 4 行：播放控制 + 工具
        int btnY = 114;
        _btnOpen = new Button { Text = "打开", Location = new Point(12, btnY), Size = new Size(64, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        _btnPlay = new Button { Text = "播放", Location = new Point(80, btnY), Size = new Size(64, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnPause = new Button { Text = "暂停", Location = new Point(148, btnY), Size = new Size(64, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnStop = new Button { Text = "停止", Location = new Point(216, btnY), Size = new Size(64, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnPrevFrame = new Button { Text = "上一帧", Location = new Point(288, btnY), Size = new Size(72, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnNextFrame = new Button { Text = "下一帧", Location = new Point(364, btnY), Size = new Size(72, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnSnapshot = new Button { Text = "截图", Location = new Point(444, btnY), Size = new Size(64, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnFullscreen = new Button { Text = "全屏", Location = new Point(512, btnY), Size = new Size(60, 28), BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        _chkTopMost = new CheckBox { Text = "置顶", Location = new Point(578, btnY + 3), Size = new Size(64, 24), ForeColor = Color.White };

        var lblSpeed = new Label { Text = "倍速", ForeColor = Color.LightGray, Font = new Font("Segoe UI", 8.5F), Location = new Point(650, btnY + 5), AutoSize = true };
        _cboSpeed = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(688, btnY + 2),
            Size = new Size(66, 24),
            FlatStyle = FlatStyle.Flat
        };

        var lblVolume = new Label { Text = "音量", ForeColor = Color.LightGray, Font = new Font("Segoe UI", 8.5F), Location = new Point(762, btnY + 5), AutoSize = true };
        _trkVolume = new TrackBar
        {
            Location = new Point(800, btnY - 2),
            Size = new Size(110, 32),
            Minimum = 0,
            Maximum = 100,
            Value = 100,
            TickStyle = TickStyle.None
        };
        _chkMute = new CheckBox { Text = "静音", Location = new Point(914, btnY + 3), Size = new Size(58, 24), ForeColor = Color.White };

        // 第 5 行：时间点
        int ptY = 150;
        _btnSetStart = new Button { Text = "设为起点(I)", Location = new Point(12, ptY), Size = new Size(92, 26), BackColor = Color.FromArgb(0, 120, 215), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _lblStartTime = new Label
        {
            Text = "起点: --:--:--.---",
            ForeColor = Color.DeepSkyBlue,
            Font = new Font("Consolas", 10F),
            Location = new Point(108, ptY + 3),
            Size = new Size(150, 18),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        _btnEditStart = new Button { Text = "编辑", Location = new Point(262, ptY), Size = new Size(42, 26), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5F), Enabled = false };

        _btnSetEnd = new Button { Text = "设为终点(O)", Location = new Point(312, ptY), Size = new Size(92, 26), BackColor = Color.FromArgb(200, 80, 0), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _lblEndTime = new Label
        {
            Text = "终点: --:--:--.---",
            ForeColor = Color.Orange,
            Font = new Font("Consolas", 10F),
            Location = new Point(408, ptY + 3),
            Size = new Size(150, 18),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        _btnEditEnd = new Button { Text = "编辑", Location = new Point(562, ptY), Size = new Size(42, 26), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5F), Enabled = false };

        _lblSegmentDuration = new Label
        {
            Text = "耗时: --:--:--.---",
            ForeColor = Color.Lime,
            Font = new Font("Consolas", 11F, FontStyle.Bold),
            Location = new Point(614, ptY + 1),
            AutoSize = true
        };

        // 第 6 行：跳转 + 循环 + 快捷键提示
        int navY = 186;
        _btnJumpStart = new Button { Text = "⇤ 起点", Location = new Point(12, navY), Size = new Size(84, 26), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _btnJumpEnd = new Button { Text = "终点 ⇥", Location = new Point(100, navY), Size = new Size(84, 26), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Enabled = false };
        _chkLoopRegion = new CheckBox { Text = "循环区间", Location = new Point(196, navY + 2), Size = new Size(96, 24), ForeColor = Color.White, Enabled = false };

        _lblShortcuts = new Label
        {
            Text = "快捷键: 空格 播放/暂停 · ←/→ 逐帧 · I/O 设起/终点 · G/H 跳起/终点 · D 截图 · F 全屏 · 滚轮 音量",
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 8.5F),
            Location = new Point(320, navY + 5),
            AutoSize = true
        };

        _controlPanel.Controls.AddRange(new Control[]
        {
            _lblFileInfo, _lblCurrentTime, lblSep, _lblDuration,
            _timeline,
            _btnOpen, _btnPlay, _btnPause, _btnStop, _btnPrevFrame, _btnNextFrame,
            _btnSnapshot, _btnFullscreen, _chkTopMost, lblSpeed, _cboSpeed, lblVolume, _trkVolume, _chkMute,
            _btnSetStart, _lblStartTime, _btnEditStart,
            _btnSetEnd, _lblEndTime, _btnEditEnd,
            _lblSegmentDuration,
            _btnJumpStart, _btnJumpEnd, _chkLoopRegion, _lblShortcuts
        });

        // ---------- 右侧测量记录面板 ----------
        _historyPanel = new Panel
        {
            Dock = DockStyle.Right,
            Width = 340,
            BackColor = Color.FromArgb(35, 35, 35)
        };

        _lblHistoryTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "  测量记录",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(50, 50, 50)
        };

        _lvHistory = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = Color.FromArgb(28, 28, 28),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9F)
        };
        _lvHistory.Columns.AddRange(new[]
        {
            new ColumnHeader { Text = "序号", Width = 46 },
            new ColumnHeader { Text = "起点", Width = 90 },
            new ColumnHeader { Text = "终点", Width = 90 },
            new ColumnHeader { Text = "耗时", Width = 100 }
        });

        _historyButtons = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Color.FromArgb(35, 35, 35) };
        _btnAddMeasure = new Button { Text = "记录本段", Location = new Point(6, 5), Size = new Size(66, 28), BackColor = Color.FromArgb(0, 120, 215), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5F) };
        _btnDelMeasure = new Button { Text = "删除", Location = new Point(76, 5), Size = new Size(52, 28), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5F) };
        _btnClearMeasure = new Button { Text = "清空", Location = new Point(132, 5), Size = new Size(52, 28), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5F) };
        _btnCopyMeasure = new Button { Text = "复制", Location = new Point(188, 5), Size = new Size(52, 28), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5F) };
        _btnExportMeasure = new Button { Text = "导出CSV", Location = new Point(244, 5), Size = new Size(88, 28), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5F) };
        _historyButtons.Controls.AddRange(new Control[] { _btnAddMeasure, _btnDelMeasure, _btnClearMeasure, _btnCopyMeasure, _btnExportMeasure });

        _historyPanel.Controls.Add(_lvHistory);
        _historyPanel.Controls.Add(_historyButtons);
        _historyPanel.Controls.Add(_lblHistoryTitle);

        // 添加控件（Fill 先加，底部面板后加以保证停靠顺序）
        this.Controls.Add(_videoPictureBox);
        this.Controls.Add(_historyPanel);
        this.Controls.Add(_controlPanel);

        this.ResumeLayout(false);
        this.PerformLayout();
    }
}
