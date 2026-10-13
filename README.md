# VideoCountMilli 视频毫秒计时器

一款面向软件测试人员的 Windows 视频耗时测量工具：播放视频的同时以**毫秒精度**显示播放时间，并可选取任意两个时间点，自动计算两点间耗时。

时间格式：`00:29:59.128` = 0 小时 29 分 59 秒 128 毫秒。

## 功能特性

- 打开常见格式视频（mp4 / mkv / avi / mov / flv / wmv / ts / webm …），支持拖拽文件到窗口
- 毫秒级当前时间显示（`HH:MM:SS.mmm`），时间直接取自视频帧的 PTS（显示时间戳），非估算值
- 完整播控：播放 / 暂停 / 停止 / 进度条拖动跳转
- **逐帧步进**（上一帧 / 下一帧），帧级精度标记时间点
- **双时间点测量**：设为起点 / 设为终点后自动显示两点间耗时（取绝对差值，设置顺序不限）
- **时间点微调**：点击时间文字或"编辑"按钮，可手动输入时间，或按 ±1 帧 / ±1 / ±10 毫秒微调，也可清除时间点
- 快捷键支持，界面底部有提示

## 快捷键

| 按键 | 功能 |
| ---- | ---- |
| 空格 | 播放 / 暂停 |
| ← / → | 上一帧 / 下一帧 |
| I | 将当前位置设为起点 |
| O | 将当前位置设为终点 |

## 下载与运行

1. 进入 [Releases](../../releases) 页面，下载最新的 `VideoCountMilli-vX.Y.Z-win-x64.zip`
2. 解压到任意目录（保持目录结构完整，`ffmpeg` 文件夹必须与 exe 同目录）
3. 双击 `VideoCountMilli.exe` 直接运行

> 自包含发布，内置 .NET 8 运行时，无需安装任何环境；支持 Windows 10/11 x64。

## 使用步骤（测量某栏目耗时）

1. 打开视频文件（按钮或直接拖入窗口）
2. 播放至栏目开始处，按 `I`（或点击"设为起点"）记录起点
3. 播放至栏目结束处，按 `O`（或点击"设为终点"）记录终点
4. 界面底部自动显示两点间耗时，精确到毫秒；如需修正，点击时间文字进行微调

> 需要帧级精度时：先暂停，再用 ←/→ 逐帧移动到目标帧后按 I/O。
> 起点和终点顺序不限，程序按两点差值的绝对值计算耗时。

## 从源码构建

环境：.NET SDK 8.0+（Windows 上也可直接用 Visual Studio 2022 打开）

```bash
cd VideoCountMilli
# 编译
dotnet build -c Release
# 发布自包含 exe（Windows x64）
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

在非 Windows 平台（Linux/macOS）构建时，csproj 已设置 `EnableWindowsTargeting=true`，可直接交叉编译出 Windows 程序。

## 技术说明

- UI：WinForms（.NET 8）
- 视频解码：[FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) 8.1 绑定 + FFmpeg 8.1.2 共享库（`VideoCountMilli/ffmpeg/` 目录随仓库分发，运行时需与 exe 同目录）
- 音频播放：[NAudio](https://github.com/naudio/NAudio)（WaveOut）
- 计时原理：解码帧的 PTS 按流时间基换算为毫秒，作为唯一时间源；进度条拖动采用关键帧回退 + 前向解码，保证显示时间与画面严格对应
- 逐帧步进：前进直接取解码器下一帧；后退先 seek 到目标帧之前的最近关键帧，再前向解码到目标帧

## 目录结构

```
video_count_milli/
├── VideoCountMilli/            # 项目源码
│   ├── Program.cs              # 入口
│   ├── MainForm.cs             # 主窗体逻辑（播控、快捷键、时间点）
│   ├── MainForm.Designer.cs    # 主窗体布局
│   ├── FFmpegPlayer.cs         # FFmpeg 播放引擎（解码/同步/逐帧步进）
│   ├── TimePointEditor.cs      # 时间点编辑对话框（微调）
│   ├── TimeFormatter.cs        # HH:MM:SS.mmm 格式化/解析
│   ├── VideoCountMilli.csproj  # 项目配置
│   └── ffmpeg/                 # FFmpeg 8.1.2 Windows x64 共享库（运行必需）
└── README.md
```

## 许可

- 本项目源码：MIT
- FFmpeg 二进制（gyan.dev full shared build）：GPL-3.0，随附分发需遵循 GPL 条款
