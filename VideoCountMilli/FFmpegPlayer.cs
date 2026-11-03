using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using NAudio.Wave;

namespace VideoCountMilli;

/// <summary>
/// 基于 FFmpeg 的视频播放器，提供帧级 PTS 毫秒精度计时。
/// 视频帧通过 FFmpeg 解码并转换为 RGB 位图，音频通过 NAudio 播放，
/// 以音频时钟作为主时钟进行音视频同步；支持逐帧步进（前一帧/后一帧）。
/// </summary>
public sealed unsafe class FFmpegPlayer : IDisposable
{
    #region 静态初始化

    static FFmpegPlayer()
    {
        // 设置 FFmpeg DLL 搜索路径（exe 同目录下的 ffmpeg 文件夹）
        string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Directory.Exists(ffmpegPath))
        {
            ffmpeg.RootPath = ffmpegPath;
            // 同时加入 PATH，确保 NativeLibrary.Load 能找到 DLL
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!path.Contains(ffmpegPath, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", ffmpegPath + Path.PathSeparator + path);
            }
        }
        // 若找不到则尝试当前目录
        else if (Directory.Exists(AppContext.BaseDirectory))
        {
            ffmpeg.RootPath = AppContext.BaseDirectory;
        }
    }

    #endregion

    #region 事件

    /// <summary>视频帧解码完成，参数为当前帧的位图。</summary>
    /// <summary>解出一帧可显示（Bitmap 由接收方负责释放）。long 为出帧序号，
    /// 用于让界面丢弃排队过程中已经过时的旧帧，避免拖动时画面滞后于手指。</summary>
    public event Action<Bitmap, long>? FrameDecoded;

    /// <summary>播放位置变化（毫秒）。</summary>
    public event Action<long>? PositionChanged;

    /// <summary>媒体时长已确定（毫秒）。</summary>
    public event Action<long>? DurationChanged;

    /// <summary>播放/暂停状态变化。</summary>
    public event Action<bool>? PlayStateChanged;

    /// <summary>播放结束。</summary>
    public event Action? PlaybackEnded;

    /// <summary>发生错误。</summary>
    public event Action<string>? Error;

    #endregion

    #region 字段

    private AVFormatContext* _fmtCtx;
    private AVCodecContext* _videoCodecCtx;
    private AVCodecContext* _audioCodecCtx;
    private int _videoStreamIndex = -1;
    private int _audioStreamIndex = -1;

    private SwsContext* _swsCtx;
    private SwrContext* _swrCtx;

    // 视频参数
    private int _videoWidth;
    private int _videoHeight;
    private AVPixelFormat _srcPixelFormat;
    private double _frameDurationMs = 40.0;  // 平均帧时长（毫秒），默认 25fps

    // 音频参数（输出）
    private const int OutSampleRate = 44100;
    private const int OutChannels = 2;
    private const AVSampleFormat OutSampleFmt = AVSampleFormat.AV_SAMPLE_FMT_S16;

    // NAudio 播放
    private WaveOut? _waveOut;
    private BufferedWaveProvider? _waveProvider;
    private long _audioBytesWritten;        // 已写入缓冲的累计字节数（自上次时钟重置起）
    private long _audioBytesConsumedBase;   // 已折算进时钟基准的消耗字节数（暂停时重基准）
    private readonly object _audioLock = new();

    // 播放控制
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private volatile bool _isDisposed;
    private Thread? _decodeThread;
    private readonly ManualResetEventSlim _pauseEvent = new(true);

    // 解码器/封装器互斥锁：解码线程、Seek、逐帧步进、预览线程共用，
    // 避免对 AVFormatContext/AVCodecContext 的并发访问
    private readonly object _decoderLock = new();

    // 拖动预览后台线程。seek + 前向解码在长 GOP 下可能几十毫秒一个来回，
    // 放在 UI 线程上会把鼠标处理和重绘一起拖住（表现为拖动“一卡一卡”）。
    // 这里只保留最新目标：手指移走后还没解完的旧目标会被主动丢弃。
    private sealed class PreviewSlot
    {
        public readonly ManualResetEventSlim Signal = new(false);
        public volatile bool StopRequested;
        public Thread? Thread;
    }

    private readonly object _previewLock = new();
    private PreviewSlot? _previewSlot;
    private long _previewTargetMs = -1;
    private long _previewGeneration;           // 每次请求 +1
    private long _previewCompletedGeneration;  // 已完成到的请求号
    private long _displaySequence;             // 出帧序号（跨解码线程与预览线程全局递增）

    // 时间状态（毫秒）
    private long _currentPositionMs;
    private long _durationMs;
    private readonly object _positionLock = new();

    // 无音频流时的壁钟；有音频时音频时钟 = 基准 + 已播放字节换算
    private readonly System.Diagnostics.Stopwatch _audioStopwatch = new();
    private long _audioClockBaseMs;   // 暂停/seek/步进时的时钟基准

    // 播放速率与音量（速率非 1.0 时音频自动静音，以免音视频不同步）
    private double _playbackRate = 1.0;
    private double _volume = 1.0;
    private bool _muted;

    #endregion

    #region 属性

    public bool IsPlaying => _isRunning && !_isPaused;
    public bool IsPaused => _isPaused;
    public long DurationMs => _durationMs;

    /// <summary>平均帧时长（毫秒）。</summary>
    public double FrameDurationMs => _frameDurationMs;

    /// <summary>帧率（fps）。</summary>
    public double Fps => _frameDurationMs > 0 ? 1000.0 / _frameDurationMs : 0;

    public long CurrentPositionMs
    {
        get
        {
            lock (_positionLock)
            {
                return _currentPositionMs;
            }
        }
    }

    #endregion

    #region 公开方法

    /// <summary>
    /// 打开媒体文件。
    /// </summary>
    public bool Open(string filePath)
    {
        try
        {
            Close();

            fixed (AVFormatContext** fmtCtxPtr = &_fmtCtx)
            {
                int ret = ffmpeg.avformat_open_input(fmtCtxPtr, filePath, null, null);
                if (ret < 0)
                {
                    OnError($"无法打开文件: {FFmpegErrorString(ret)}");
                    return false;
                }
            }

            if (ffmpeg.avformat_find_stream_info(_fmtCtx, null) < 0)
            {
                OnError("无法读取流信息");
                return false;
            }

            // 时长
            if (_fmtCtx->duration != ffmpeg.AV_NOPTS_VALUE)
            {
                _durationMs = (long)(_fmtCtx->duration * 1000.0 / ffmpeg.AV_TIME_BASE);
                DurationChanged?.Invoke(_durationMs);
            }

            // 查找视频流
            _videoStreamIndex = ffmpeg.av_find_best_stream(_fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (_videoStreamIndex < 0)
            {
                OnError("未找到视频流");
                return false;
            }

            // 计算平均帧时长（用于逐帧步进）
            AVStream* vStream = _fmtCtx->streams[_videoStreamIndex];
            AVRational frameRate = vStream->avg_frame_rate;
            if (frameRate.num <= 0 || frameRate.den <= 0)
            {
                frameRate = vStream->r_frame_rate;
            }
            if (frameRate.num > 0 && frameRate.den > 0)
            {
                _frameDurationMs = 1000.0 * frameRate.den / frameRate.num;
            }

            // 查找音频流（可选）
            _audioStreamIndex = ffmpeg.av_find_best_stream(_fmtCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);

            // 打开视频解码器
            if (!OpenCodec(_videoStreamIndex, out _videoCodecCtx, AVMediaType.AVMEDIA_TYPE_VIDEO))
            {
                return false;
            }

            _videoWidth = _videoCodecCtx->width;
            _videoHeight = _videoCodecCtx->height;
            _srcPixelFormat = _videoCodecCtx->pix_fmt;

            // 初始化 sws 上下文（转 RGB24）
            _swsCtx = ffmpeg.sws_getContext(
                _videoWidth, _videoHeight, _srcPixelFormat,
                _videoWidth, _videoHeight, AVPixelFormat.AV_PIX_FMT_RGB24,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);

            // 打开音频解码器
            if (_audioStreamIndex >= 0)
            {
                if (OpenCodec(_audioStreamIndex, out _audioCodecCtx, AVMediaType.AVMEDIA_TYPE_AUDIO))
                {
                    InitAudioResampler();
                    InitAudioPlayer();
                }
                else
                {
                    _audioStreamIndex = -1;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            OnError($"打开文件异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 开始/继续播放。
    /// </summary>
    public void Play()
    {
        if (_isDisposed || _fmtCtx == null) return;

        // 拖动留下的待解码目标不能再解了：它会把已经开始播放的解码器和时钟拽回旧位置
        CancelPendingPreview();
        WaitForPreviewIdle(150);

        if (!_isRunning)
        {
            // 已播放到结尾时，从头开始
            lock (_positionLock)
            {
                if (_durationMs > 0 && _currentPositionMs >= _durationMs - 1)
                {
                    _currentPositionMs = 0;
                }
            }
            if (CurrentPositionMs == 0)
            {
                SeekInternal(0);
            }

            _audioClockBaseMs = CurrentPositionMs;
            _waveProvider?.ClearBuffer();
            lock (_audioLock)
            {
                _audioBytesWritten = 0;
                _audioBytesConsumedBase = 0;
            }
            _audioStopwatch.Restart();
            _isRunning = true;
            _isPaused = false;
            _pauseEvent.Set();
            _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "FFmpegDecode" };
            _decodeThread.Start();
        }
        else if (_isPaused)
        {
            _isPaused = false;
            // 时钟基准已在 Pause 时冻结（音频路径已重基准），
            // 壁钟路径从当前位置重新计时
            _audioStopwatch.Restart();
            if (_playbackRate == 1.0)
            {
                _waveOut?.Play();
            }
            _pauseEvent.Set();
        }

        PlayStateChanged?.Invoke(true);
    }

    /// <summary>
    /// 暂停播放。
    /// </summary>
    public void Pause()
    {
        if (_isDisposed || !_isRunning || _isPaused) return;

        _isPaused = true;
        _pauseEvent.Reset();
        _waveOut?.Pause();

        // 冻结主时钟：
        // 音频路径（原速）把已消耗字节折算进基准，否则恢复播放后会把
        // 暂停前已播放的时长重复计入时钟，导致画面超前；
        // 壁钟路径把基准设为当前位置并停表
        if (_waveProvider != null && _playbackRate == 1.0)
        {
            RebaseAudioClock();
        }
        else
        {
            _audioClockBaseMs = CurrentPositionMs;
        }
        _audioStopwatch.Stop();

        PlayStateChanged?.Invoke(false);
    }

    /// <summary>
    /// 停止并回到起始位置。
    /// </summary>
    public void Stop()
    {
        if (_isDisposed) return;
        StopInternal(notifyUi: true);
    }

    private void StopInternal(bool notifyUi)
    {
        _isRunning = false;
        _isPaused = false;
        _pauseEvent.Set();
        _decodeThread?.Join(2000);
        StopPreviewThread();
        _waveOut?.Stop();
        _waveProvider?.ClearBuffer();
        _audioStopwatch.Reset();
        _audioBytesWritten = 0;
        _audioBytesConsumedBase = 0;
        lock (_positionLock) { _currentPositionMs = 0; }
        if (!notifyUi) return; // 退出流程：只停线程，不再回调界面
        PositionChanged?.Invoke(0);
        PlayStateChanged?.Invoke(false);
    }

    /// <summary>
    /// 跳转到指定毫秒位置。
    /// </summary>
    public void Seek(long positionMs)
    {
        if (_isDisposed || _fmtCtx == null) return;
        CancelPendingPreview();

        positionMs = Math.Clamp(positionMs, 0, Math.Max(0, _durationMs));
        SeekInternal(positionMs);
        PositionChanged?.Invoke(positionMs);
    }

    /// <summary>
    /// 请求跳转到指定毫秒并预览该处画面：seek 与解码跑在独立线程上，UI 线程不等待。
    /// 拖动进度条时每个鼠标移动都会调用，只有最新目标会被解码，
    /// 已经开始但已过时的解码会在下一个包之前主动退出。
    /// </summary>
    public void BeginSeekPreview(long positionMs)
    {
        if (_isDisposed || _fmtCtx == null || _videoCodecCtx == null) return;

        positionMs = Math.Clamp(positionMs, 0, Math.Max(0, _durationMs));

        lock (_previewLock)
        {
            _previewTargetMs = positionMs;
            _previewGeneration++;
            EnsurePreviewThread();
        }
    }

    /// <summary>
    /// 等后台预览把最新目标解出来（松手恢复播放前调用，避免先闪一帧旧画面）。
    /// 超时立即返回，不让长 GOP 卡住界面。
    /// </summary>
    public void WaitForPreviewIdle(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            long pending;
            lock (_previewLock)
            {
                pending = _previewGeneration - _previewCompletedGeneration;
            }
            if (pending <= 0) return;
            Thread.Sleep(4);
        }
    }

    /// <summary>
    /// 丢弃尚未处理的预览目标（正式播放 / 定位 / 逐帧步进前调用）。
    /// 代数 +1 让在飞的解码在下一个包之前主动退出，同时把完成号对齐，表示没有待办。
    /// </summary>
    public void CancelPendingPreview()
    {
        lock (_previewLock)
        {
            _previewGeneration++;
            _previewCompletedGeneration = _previewGeneration;
        }
    }

    private void EnsurePreviewThread()
    {
        PreviewSlot? slot = _previewSlot;
        if (slot == null || slot.StopRequested || slot.Thread is not { IsAlive: true })
        {
            slot = new PreviewSlot();
            slot.Thread = new Thread(() => PreviewLoop(slot)) { IsBackground = true, Name = "FFmpegPreview" };
            _previewSlot = slot;
            slot.Thread.Start();
        }
        slot.Signal.Set();
    }

    private void StopPreviewThread()
    {
        PreviewSlot? slot;
        lock (_previewLock)
        {
            slot = _previewSlot;
            _previewSlot = null;
            if (slot != null)
            {
                _previewCompletedGeneration = _previewGeneration;
                slot.StopRequested = true;
                slot.Signal.Set();
            }
        }
        if (slot?.Thread == null) return;

        if (slot.Thread.Join(2000))
        {
            slot.Signal.Dispose();
        }
        // .join 超时说明线程还压在解码里：让它自行退出，
        // 此时不再复用该 slot，也不释放它还在等待的事件句柄
    }

    private void PreviewLoop(PreviewSlot slot)
    {
        while (!slot.StopRequested && !_isDisposed)
        {
            slot.Signal.Wait(200);
            if (slot.StopRequested || _isDisposed) break;

            long target;
            long generation;
            bool hasWork;
            lock (_previewLock)
            {
                hasWork = _previewCompletedGeneration < _previewGeneration;
                // 必须在锁内清信号：与 BeginSeekPreview 的 Set 互斥，
                // 既不会漏掉刚提出的目标，也不会带着置位空转
                slot.Signal.Reset();
                target = _previewTargetMs;
                generation = _previewGeneration;
            }
            if (!hasWork) continue;

            long completedGeneration;
            try
            {
                completedGeneration = SeekPreviewCore(target, generation, slot);
            }
            catch (Exception ex)
            {
                OnError($"画面预览异常: {ex.Message}");
                completedGeneration = generation; // 失败也算处理完，避免等待方白等一个超时
            }

            if (completedGeneration < 0) continue; // 被更新的目标取代，接着解最新的

            lock (_previewLock)
            {
                if (completedGeneration > _previewCompletedGeneration)
                {
                    _previewCompletedGeneration = completedGeneration;
                }
            }
        }
    }

    /// <summary>
    /// seek 到目标时刻之前最近的关键帧，向后解码直到第一帧 pts &gt;= 目标并显示。
    /// 期间若 _previewGeneration 已经前进（手指移走了）或线程被要求停止，
    /// 立即放弃并返回 -1，把解码器让给下一个目标。
    /// </summary>
    private long SeekPreviewCore(long positionMs, long generation, PreviewSlot slot)
    {
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            lock (_decoderLock)
            {
                if (_isDisposed || _fmtCtx == null || _videoCodecCtx == null) return generation;
                if (IsPreviewStale(slot, generation)) return -1;

                long targetTimestamp = (long)(positionMs * ffmpeg.AV_TIME_BASE / 1000.0);
                int ret = ffmpeg.av_seek_frame(_fmtCtx, -1, targetTimestamp,
                    ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (ret < 0)
                {
                    ffmpeg.avformat_seek_file(_fmtCtx, -1, 0, targetTimestamp, targetTimestamp, 0);
                }

                ffmpeg.avcodec_flush_buffers(_videoCodecCtx);
                if (_audioCodecCtx != null)
                    ffmpeg.avcodec_flush_buffers(_audioCodecCtx);

                ResetAudioClock(positionMs);

                lock (_positionLock)
                {
                    _currentPositionMs = positionMs;
                }

                // 向前解码，显示第一帧 pts >= 目标时刻的画面
                while (true)
                {
                    // 每个包之前检查一次：手指早就移走了就别再解下去了
                    if (IsPreviewStale(slot, generation)) return -1;

                    int readRet = ffmpeg.av_read_frame(_fmtCtx, packet);
                    if (readRet < 0)
                    {
                        // 到达文件末尾：刷新解码器，显示剩余最后一帧
                        ffmpeg.avcodec_send_packet(_videoCodecCtx, null);
                        DrainAndDisplay(frame, long.MinValue / 2);
                        return generation;
                    }

                    if (packet->stream_index == _videoStreamIndex)
                    {
                        ffmpeg.avcodec_send_packet(_videoCodecCtx, packet);
                        bool shown = DrainAndDisplay(frame, positionMs);
                        ffmpeg.av_packet_unref(packet);
                        if (shown) return generation;
                    }
                    else
                    {
                        ffmpeg.av_packet_unref(packet);
                    }
                }
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
        }
    }

    /// <summary>本次预览是否已无意义：被更新的目标取代、正在关闭播放器，或线程正在停止。</summary>
    private bool IsPreviewStale(PreviewSlot slot, long generation) =>
        slot.StopRequested || _isDisposed || Volatile.Read(ref _previewGeneration) != generation;

    /// <summary>
    /// 逐帧步进。direction &gt; 0 为下一帧，direction &lt; 0 为上一帧。
    /// 步进会自动暂停播放，位置精确到目标帧的 PTS。
    /// </summary>
    public void StepFrame(int direction)
    {
        if (_isDisposed || _fmtCtx == null || _videoCodecCtx == null) return;
        CancelPendingPreview();

        if (IsPlaying)
        {
            Pause();
        }

        if (direction >= 0)
        {
            StepForward();
        }
        else
        {
            StepBackward();
        }
    }

    /// <summary>
    /// 设置播放速率（0.25 ~ 4.0）。非 1.0 时音频自动静音（避免音视频不同步），
    /// 视频按目标速率前进（加速时必要时丢帧追赶）。
    /// </summary>
    public void SetPlaybackRate(double rate)
    {
        rate = Math.Clamp(rate, 0.25, 4.0);
        if (Math.Abs(rate - _playbackRate) < 1e-6) return;

        lock (_decoderLock)
        {
            _playbackRate = rate;

            // 重置时钟基准，避免速率切换时位置跳变
            ResetAudioClock(CurrentPositionMs);

            if (rate != 1.0)
            {
                // 非原速不输出音频
                _waveOut?.Pause();
                _waveProvider?.ClearBuffer();
            }
            else if (!_isPaused)
            {
                _waveOut?.Play();
            }
        }
    }

    /// <summary>设置音量（0.0 ~ 1.0）。</summary>
    public void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0.0, 1.0);
        ApplyVolume();
    }

    /// <summary>静音开关。</summary>
    public void SetMuted(bool muted)
    {
        _muted = muted;
        ApplyVolume();
    }

    private void ApplyVolume()
    {
        if (_waveOut == null) return;
        try { _waveOut.Volume = (float)(_muted ? 0.0 : _volume); }
        catch { /* 部分设备不支持音量控制 */ }
    }

    public void Close()
    {
        // Dispose 已经把 _isDisposed 置位，此时 public Stop() 会直接返回；
        // 必须走 StopInternal 把解码线程和预览线程 join 掉，
        // 否则下面 Cleanup() 释放上下文时线程还在 av_read_frame 里用它们
        StopInternal(notifyUi: !_isDisposed);
        Cleanup();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Close();
        _pauseEvent.Dispose();
    }

    #endregion

    #region 私有方法 - 解码器初始化

    private bool OpenCodec(int streamIndex, out AVCodecContext* codecCtx, AVMediaType type)
    {
        codecCtx = null;
        AVStream* stream = _fmtCtx->streams[streamIndex];
        AVCodecParameters* codecpar = stream->codecpar;

        AVCodec* codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        if (codec == null)
        {
            OnError($"找不到{type}解码器");
            return false;
        }

        codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (codecCtx == null) return false;

        if (ffmpeg.avcodec_parameters_to_context(codecCtx, codecpar) < 0)
        {
            OnError($"无法复制{type}编解码参数");
            return false;
        }

        if (ffmpeg.avcodec_open2(codecCtx, codec, null) < 0)
        {
            OnError($"无法打开{type}解码器");
            return false;
        }

        return true;
    }

    private void InitAudioResampler()
    {
        AVCodecContext* ctx = _audioCodecCtx;

        // 输出声道布局（立体声）
        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&outLayout, OutChannels);

        // 输入声道布局（从解码器上下文复制）
        AVChannelLayout inLayout = ctx->ch_layout;

        SwrContext* swrCtx = null;
        int ret = ffmpeg.swr_alloc_set_opts2(
            &swrCtx,
            &outLayout, OutSampleFmt, OutSampleRate,
            &inLayout, ctx->sample_fmt, ctx->sample_rate,
            0, null);

        if (ret < 0 || swrCtx == null || ffmpeg.swr_init(swrCtx) < 0)
        {
            OnError("音频重采样初始化失败");
            if (swrCtx != null) ffmpeg.swr_free(&swrCtx);
            _swrCtx = null;
        }
        else
        {
            _swrCtx = swrCtx;
        }
    }

    private void InitAudioPlayer()
    {
        var waveFormat = new WaveFormat(OutSampleRate, 16, OutChannels);
        _waveProvider = new BufferedWaveProvider(waveFormat)
        {
            BufferLength = OutSampleRate * OutChannels * 2 * 10, // 10 秒缓冲
            DiscardOnBufferOverflow = true
        };
        _waveOut = new WaveOut();
        _waveOut.Init(_waveProvider);
    }

    #endregion

    #region 私有方法 - Seek 与逐帧步进

    /// <summary>执行 seek（不做事件通知，由调用方决定）。</summary>
    private void SeekInternal(long positionMs)
    {
        lock (_decoderLock)
        {
            long targetTimestamp = (long)(positionMs * ffmpeg.AV_TIME_BASE / 1000.0);
            int ret = ffmpeg.av_seek_frame(_fmtCtx, -1, targetTimestamp,
                ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (ret < 0)
            {
                ffmpeg.avformat_seek_file(_fmtCtx, -1, 0, targetTimestamp, targetTimestamp, 0);
            }

            // 刷新解码器缓冲
            if (_videoCodecCtx != null)
                ffmpeg.avcodec_flush_buffers(_videoCodecCtx);
            if (_audioCodecCtx != null)
                ffmpeg.avcodec_flush_buffers(_audioCodecCtx);

            ResetAudioClock(positionMs);

            lock (_positionLock)
            {
                _currentPositionMs = positionMs;
            }
        }
    }

    /// <summary>步进到下一帧：优先取解码器缓冲中的帧，否则继续读包。</summary>
    private void StepForward()
    {
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        bool flushed = false;
        try
        {
            lock (_decoderLock)
            {
                ResetAudioClock(GetCurrentPositionMs());

                while (true)
                {
                    // 先尝试从解码器取帧（可能还有缓冲帧）
                    int r = ffmpeg.avcodec_receive_frame(_videoCodecCtx, frame);
                    if (r == 0)
                    {
                        DisplayFrame(frame);
                        return;
                    }
                    if (r != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        return; // EOF 或解码错误
                    }
                    if (flushed)
                    {
                        return; // 已刷新过仍无帧
                    }

                    int readRet = ffmpeg.av_read_frame(_fmtCtx, packet);
                    if (readRet < 0)
                    {
                        // 文件读完，刷新解码器取剩余帧
                        ffmpeg.avcodec_send_packet(_videoCodecCtx, null);
                        flushed = true;
                        continue;
                    }

                    if (packet->stream_index == _videoStreamIndex)
                    {
                        ffmpeg.avcodec_send_packet(_videoCodecCtx, packet);
                    }
                    // 逐帧时跳过音频包（恢复播放时从当前位置继续）

                    ffmpeg.av_packet_unref(packet);
                }
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
        }
    }

    /// <summary>步进到上一帧：向前 seek 到目标帧之前，再向前解码到目标帧。</summary>
    private void StepBackward()
    {
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            lock (_decoderLock)
            {
                long frameMs = Math.Max(1, (long)Math.Round(_frameDurationMs));
                long cur = GetCurrentPositionMs();
                long target = Math.Max(0, cur - frameMs);

                // 向后 seek 到目标帧的前一个位置（保证能解出目标帧）
                long seekPos = Math.Max(0, target - frameMs);
                long ts = (long)(seekPos * ffmpeg.AV_TIME_BASE / 1000.0);
                int ret = ffmpeg.av_seek_frame(_fmtCtx, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (ret < 0)
                {
                    ffmpeg.avformat_seek_file(_fmtCtx, -1, 0, ts, ts, 0);
                }
                ffmpeg.avcodec_flush_buffers(_videoCodecCtx);
                if (_audioCodecCtx != null)
                    ffmpeg.avcodec_flush_buffers(_audioCodecCtx);
                ResetAudioClock(target);

                // 向前解码，直到拿到 pts >= 目标的帧
                long threshold = target - frameMs / 2;

                while (true)
                {
                    int readRet = ffmpeg.av_read_frame(_fmtCtx, packet);
                    if (readRet < 0)
                    {
                        // 文件读完，刷新解码器并显示剩余帧
                        ffmpeg.avcodec_send_packet(_videoCodecCtx, null);
                        DrainAndDisplay(frame, long.MinValue / 2);
                        return;
                    }

                    if (packet->stream_index == _videoStreamIndex)
                    {
                        ffmpeg.avcodec_send_packet(_videoCodecCtx, packet);
                        if (DrainAndDisplay(frame, threshold))
                        {
                            return;
                        }
                    }

                    ffmpeg.av_packet_unref(packet);
                }
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
        }
    }

    /// <summary>
    /// 从解码器取帧并显示第一帧 pts &gt;= threshold 的帧；无合适帧返回 false。
    /// </summary>
    private bool DrainAndDisplay(AVFrame* frame, long threshold)
    {
        int r;
        while ((r = ffmpeg.avcodec_receive_frame(_videoCodecCtx, frame)) == 0)
        {
            long ptsMs = GetFramePtsMs(frame, _videoStreamIndex);
            if (ptsMs < 0 || ptsMs >= threshold)
            {
                DisplayFrame(frame);
                ffmpeg.av_frame_unref(frame);
                return true;
            }
            ffmpeg.av_frame_unref(frame);
        }
        return false;
    }

    /// <summary>显示一帧：更新位置（帧 PTS）并通知界面。</summary>
    private void DisplayFrame(AVFrame* frame)
    {
        long ptsMs = GetFramePtsMs(frame, _videoStreamIndex);
        if (ptsMs >= 0)
        {
            lock (_positionLock)
            {
                _currentPositionMs = ptsMs;
            }
            PositionChanged?.Invoke(ptsMs);
        }

        Bitmap? bmp = ConvertFrameToBitmap(frame);
        if (bmp != null)
        {
            long seq = Interlocked.Increment(ref _displaySequence);
            FrameDecoded?.Invoke(bmp, seq);
        }
    }

    /// <summary>重置音频缓冲与时钟基准。</summary>
    private void ResetAudioClock(long baseMs)
    {
        _waveProvider?.ClearBuffer();
        lock (_audioLock)
        {
            _audioBytesWritten = 0;
            _audioBytesConsumedBase = 0;
        }
        _audioClockBaseMs = baseMs;
        _audioStopwatch.Restart();
        if (_isPaused)
        {
            _audioStopwatch.Stop();
        }
    }

    /// <summary>
    /// 把已消耗的音频字节数折算进时钟基准（暂停时调用），
    /// 使恢复播放后主时钟从暂停点继续，而不是把暂停前已播放的时长重复计入。
    /// </summary>
    private void RebaseAudioClock()
    {
        if (_waveProvider == null) return;

        lock (_audioLock)
        {
            long consumed = _audioBytesWritten - _waveProvider.BufferedBytes - _audioBytesConsumedBase;
            if (consumed > 0)
            {
                double bytesPerSec = OutSampleRate * OutChannels * 2;
                _audioClockBaseMs += (long)(consumed / bytesPerSec * 1000.0);
                _audioBytesConsumedBase = _audioBytesWritten - _waveProvider.BufferedBytes;
            }
        }
    }

    #endregion

    #region 私有方法 - 解码循环

    private void DecodeLoop()
    {
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();

        try
        {
            while (_isRunning && !_isDisposed)
            {
                _pauseEvent.Wait();
                if (!_isRunning) break;

                int readRet;
                bool isEof = false;
                lock (_decoderLock)
                {
                    if (!_isRunning) break;

                    readRet = ffmpeg.av_read_frame(_fmtCtx, packet);
                    if (readRet < 0)
                    {
                        isEof = readRet == ffmpeg.AVERROR_EOF;
                    }
                    else
                    {
                        if (packet->stream_index == _videoStreamIndex)
                        {
                            DecodeVideoFrame(packet, frame);
                        }
                        else if (packet->stream_index == _audioStreamIndex)
                        {
                            DecodeAudioFrame(packet, frame);
                        }
                        ffmpeg.av_packet_unref(packet);
                    }
                }

                if (isEof)
                {
                    // 刷新解码器中的剩余帧
                    lock (_decoderLock)
                    {
                        FlushVideoDecoder(frame);
                    }
                    // 等待音频播放完
                    DrainAudio();
                    PlaybackEnded?.Invoke();
                    break;
                }
                if (readRet < 0)
                {
                    break; // 读取错误
                }
            }
        }
        catch (Exception ex)
        {
            OnError($"解码循环异常: {ex.Message}");
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
            _isRunning = false;
            PlayStateChanged?.Invoke(false);
        }
    }

    private void DecodeVideoFrame(AVPacket* packet, AVFrame* frame)
    {
        int ret = ffmpeg.avcodec_send_packet(_videoCodecCtx, packet);
        if (ret < 0) return;

        while (ret >= 0)
        {
            ret = ffmpeg.avcodec_receive_frame(_videoCodecCtx, frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0) return;

            // 音视频同步：等待音频时钟（在显示前）
            long ptsMs = GetFramePtsMs(frame, _videoStreamIndex);
            if (ptsMs >= 0)
            {
                SyncToAudio(ptsMs);

                // 加速播放时，若该帧落后时钟超过一帧，则丢帧追赶
                if (_playbackRate > 1.0 && ptsMs < GetAudioClockMs() - _frameDurationMs)
                {
                    ffmpeg.av_frame_unref(frame);
                    continue;
                }
            }

            DisplayFrame(frame);
            ffmpeg.av_frame_unref(frame);
        }
    }

    private void DecodeAudioFrame(AVPacket* packet, AVFrame* frame)
    {
        if (_audioCodecCtx == null || _swrCtx == null || _waveProvider == null) return;

        // 非原速播放时跳过音频输出，避免音视频不同步
        if (_playbackRate != 1.0) return;

        int ret = ffmpeg.avcodec_send_packet(_audioCodecCtx, packet);
        if (ret < 0) return;

        while (ret >= 0)
        {
            ret = ffmpeg.avcodec_receive_frame(_audioCodecCtx, frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0) return;

            // 重采样为 s16le stereo 44100
            int outSamples = ffmpeg.swr_get_out_samples(_swrCtx, frame->nb_samples);
            int bufferSize = ffmpeg.av_samples_get_buffer_size(
                null, OutChannels, outSamples, OutSampleFmt, 1);

            byte[] outBuffer = new byte[bufferSize];
            byte*[] inPlanes =
            {
                frame->data[0], frame->data[1], frame->data[2], frame->data[3],
                frame->data[4], frame->data[5], frame->data[6], frame->data[7]
            };
            fixed (byte* outBufPtr = outBuffer)
            fixed (byte** inPtr = inPlanes)
            {
                byte* outPlane = outBufPtr;
                int converted = ffmpeg.swr_convert(_swrCtx, &outPlane, outSamples,
                    inPtr, frame->nb_samples);
                if (converted > 0)
                {
                    int bytes = converted * OutChannels * 2; // s16 = 2 bytes
                    _waveProvider.AddSamples(outBuffer, 0, bytes);
                    lock (_audioLock)
                    {
                        _audioBytesWritten += bytes;
                    }
                    if (_waveOut != null && _waveOut.PlaybackState != PlaybackState.Playing && !_isPaused)
                    {
                        _waveOut.Play();
                    }
                }
            }

            ffmpeg.av_frame_unref(frame);
        }
    }

    private void FlushVideoDecoder(AVFrame* frame)
    {
        // 向解码器发送 NULL packet 以刷新
        ffmpeg.avcodec_send_packet(_videoCodecCtx, null);
        while (ffmpeg.avcodec_receive_frame(_videoCodecCtx, frame) >= 0)
        {
            DisplayFrame(frame);
            ffmpeg.av_frame_unref(frame);
        }
    }

    private void DrainAudio()
    {
        // 等待音频缓冲播放完毕（最多等 3 秒）
        if (_waveProvider == null) return;
        DateTime start = DateTime.Now;
        while (_waveProvider.BufferedDuration.TotalMilliseconds > 100 &&
               (DateTime.Now - start).TotalSeconds < 3)
        {
            Thread.Sleep(20);
        }
    }

    #endregion

    #region 私有方法 - 工具

    private long GetFramePtsMs(AVFrame* frame, int streamIndex)
    {
        AVStream* stream = _fmtCtx->streams[streamIndex];
        long pts = frame->best_effort_timestamp;
        if (pts == ffmpeg.AV_NOPTS_VALUE) pts = frame->pts;
        if (pts == ffmpeg.AV_NOPTS_VALUE) return -1;

        double timeBase = ffmpeg.av_q2d(stream->time_base);
        return (long)(pts * timeBase * 1000.0);
    }

    /// <summary>
    /// 将视频帧同步到音频时钟。
    /// </summary>
    private void SyncToAudio(long framePtsMs)
    {
        long audioClock = GetAudioClockMs();
        long diff = framePtsMs - audioClock;

        // 如果视频超前音频，等待
        if (diff > 0)
        {
            int waitMs = (int)Math.Min(diff, 100L); // 最多等 100ms
            if (waitMs > 0)
            {
                Thread.Sleep(waitMs);
            }
        }
        // 如果视频落后音频超过阈值，丢帧（不做额外处理，继续下一个）
    }

    /// <summary>
    /// 获取当前主时钟（毫秒）。有音频流时基于已播放音频字节；
    /// 无音频流时使用壁钟（基于上次基准 + 经过时间）。
    /// </summary>
    private long GetAudioClockMs()
    {
        if (_waveProvider == null || _playbackRate != 1.0)
        {
            // 无音频或非原速：使用（按速率缩放的）壁钟
            return _audioClockBaseMs + (long)(_audioStopwatch.ElapsedMilliseconds * _playbackRate);
        }

        double bytesPerSec = OutSampleRate * OutChannels * 2;
        long bufferedBytes = _waveProvider.BufferedBytes;

        long playedBytes;
        lock (_audioLock)
        {
            playedBytes = _audioBytesWritten - bufferedBytes - _audioBytesConsumedBase;
        }

        long clockFromBytes = (long)(playedBytes / bytesPerSec * 1000.0);

        // 结合基准和已播放量
        return _audioClockBaseMs + clockFromBytes;
    }

    private long GetCurrentPositionMs()
    {
        lock (_positionLock) { return _currentPositionMs; }
    }

    /// <summary>
    /// 把解码帧转成 24 位位图。直接把 sws 输出写进 LockBits 的 Scan0：
    /// 既省掉每帧一次约 width*height*3 的大数组分配（1080p 约 6MB，落在大对象堆上，
    /// 拖动预览时会造成明显 GC 压力），也避免按“行长 = 宽*3”假设拷贝而漏掉
    /// GDI+ 的 4 字节行对齐（宽度不是 4 的倍数时画面会斜）。
    /// </summary>
    private Bitmap? ConvertFrameToBitmap(AVFrame* frame)
    {
        if (_swsCtx == null) return null;

        byte*[] srcData =
        {
            frame->data[0], frame->data[1], frame->data[2], frame->data[3],
            frame->data[4], frame->data[5], frame->data[6], frame->data[7]
        };
        int[] srcStride =
        {
            (int)frame->linesize[0], (int)frame->linesize[1], (int)frame->linesize[2], (int)frame->linesize[3],
            (int)frame->linesize[4], (int)frame->linesize[5], (int)frame->linesize[6], (int)frame->linesize[7]
        };

        Bitmap bmp = new(_videoWidth, _videoHeight, PixelFormat.Format24bppRgb);
        BitmapData? bmpData = null;
        try
        {
            bmpData = bmp.LockBits(
                new Rectangle(0, 0, _videoWidth, _videoHeight),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            byte*[] dstData = { (byte*)bmpData.Scan0.ToInt64() };
            int[] dstStride = { bmpData.Stride };

            int scaled = ffmpeg.sws_scale(_swsCtx,
                srcData, srcStride, 0, _videoHeight,
                dstData, dstStride);

            if (scaled < 0)
            {
                bmp.UnlockBits(bmpData);
                bmp.Dispose();
                return null;
            }

            // GDI+ 把行补齐到 4 字节，补齐的尾部不属于画面；
            // LockBits 的内存不是零初始化的，不清的话非 4 倍宽的视频右缘会有噪点
            int pad = bmpData.Stride - _videoWidth * 3;
            if (pad > 0)
            {
                byte* scan = (byte*)bmpData.Scan0.ToInt64();
                for (int y = 0; y < _videoHeight; y++)
                {
                    byte* rowEnd = scan + (long)y * bmpData.Stride + _videoWidth * 3;
                    for (int i = 0; i < pad; i++) rowEnd[i] = 0;
                }
            }
            bmp.UnlockBits(bmpData);
            bmpData = null;
            return bmp;
        }
        catch
        {
            // 转换失败：这一帧不显示，但要把锁过的内存还回去并丢弃位图
            if (bmpData != null) bmp.UnlockBits(bmpData);
            bmp.Dispose();
            return null;
        }
    }

    private void OnError(string msg)
    {
        Error?.Invoke(msg);
    }

    private static string FFmpegErrorString(int errorCode)
    {
        byte[] buffer = new byte[256];
        fixed (byte* ptr = buffer)
        {
            ffmpeg.av_strerror(errorCode, ptr, (ulong)buffer.Length);
            return Marshal.PtrToStringAnsi((IntPtr)ptr) ?? $"error code {errorCode}";
        }
    }

    private void Cleanup()
    {
        if (_waveOut != null)
        {
            try { _waveOut.Stop(); _waveOut.Dispose(); } catch { }
            _waveOut = null;
        }
        _waveProvider = null;

        // 解码线程与预览线程都在 _decoderLock 内碰这些上下文，释放时抢同一把锁；
        // 线程已经停干净时拿锁是即时的
        lock (_decoderLock)
        {
            if (_swrCtx != null) { SwrContext* swr = _swrCtx; ffmpeg.swr_free(&swr); _swrCtx = null; }
            if (_swsCtx != null) { ffmpeg.sws_freeContext(_swsCtx); _swsCtx = null; }
            if (_videoCodecCtx != null) { AVCodecContext* vc = _videoCodecCtx; ffmpeg.avcodec_free_context(&vc); _videoCodecCtx = null; }
            if (_audioCodecCtx != null) { AVCodecContext* ac = _audioCodecCtx; ffmpeg.avcodec_free_context(&ac); _audioCodecCtx = null; }
            if (_fmtCtx != null) { AVFormatContext* fc = _fmtCtx; ffmpeg.avformat_close_input(&fc); _fmtCtx = null; }
        }

        _videoStreamIndex = -1;
        _audioStreamIndex = -1;
        _audioBytesWritten = 0;
        _audioBytesConsumedBase = 0;
        _audioClockBaseMs = 0;
        _audioStopwatch.Reset();
        lock (_positionLock) { _currentPositionMs = 0; }
        _durationMs = 0;
    }

    #endregion
}
