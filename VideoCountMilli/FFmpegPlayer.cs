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
/// 以音频时钟作为主时钟进行音视频同步。
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
    public event Action<Bitmap>? FrameDecoded;

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

    // 视频帧尺寸
    private int _videoWidth;
    private int _videoHeight;
    private AVPixelFormat _srcPixelFormat;

    // 音频参数（输出）
    private const int OutSampleRate = 44100;
    private const int OutChannels = 2;
    private const AVSampleFormat OutSampleFmt = AVSampleFormat.AV_SAMPLE_FMT_S16;

    // NAudio 播放
    private WaveOut? _waveOut;
    private BufferedWaveProvider? _waveProvider;
    private long _audioBytesWritten;   // 已写入缓冲的字节数
    private readonly object _audioLock = new();

    // 播放控制
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private volatile bool _isDisposed;
    private Thread? _decodeThread;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private readonly object _seekLock = new();

    // 时间状态（毫秒）
    private long _currentPositionMs;
    private long _durationMs;
    private readonly object _positionLock = new();

    // 用于估算音频时钟的 Stopwatch
    private readonly System.Diagnostics.Stopwatch _audioStopwatch = new();
    private long _audioClockBaseMs;   // 暂停/seek 时的基准音频时钟

    #endregion

    #region 属性

    public bool IsPlaying => _isRunning && !_isPaused;
    public bool IsPaused => _isPaused;
    public long DurationMs => _durationMs;
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

        if (!_isRunning)
        {
            _isRunning = true;
            _isPaused = false;
            _pauseEvent.Set();
            _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "FFmpegDecode" };
            _decodeThread.Start();
        }
        else if (_isPaused)
        {
            _isPaused = false;
            // 恢复音频时钟基准
            _audioClockBaseMs = GetCurrentPositionMs();
            _audioStopwatch.Restart();
            _waveOut?.Play();
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
        // 保存当前音频时钟
        _audioClockBaseMs = GetCurrentPositionMs();
        _audioStopwatch.Stop();

        PlayStateChanged?.Invoke(false);
    }

    /// <summary>
    /// 停止并关闭。
    /// </summary>
    public void Stop()
    {
        if (_isDisposed) return;
        _isRunning = false;
        _isPaused = false;
        _pauseEvent.Set();
        _decodeThread?.Join(500);
        _waveOut?.Stop();
        _waveProvider?.ClearBuffer();
        _audioStopwatch.Reset();
        _audioBytesWritten = 0;
        lock (_positionLock) { _currentPositionMs = 0; }
        PositionChanged?.Invoke(0);
        PlayStateChanged?.Invoke(false);
    }

    /// <summary>
    /// 跳转到指定毫秒位置。
    /// </summary>
    public void Seek(long positionMs)
    {
        if (_isDisposed || _fmtCtx == null) return;

        lock (_seekLock)
        {
            long targetTimestamp = (long)(positionMs * ffmpeg.AV_TIME_BASE / 1000.0);
            int ret = ffmpeg.av_seek_frame(_fmtCtx, -1, targetTimestamp,
                ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (ret < 0)
            {
                ret = ffmpeg.avformat_seek_file(_fmtCtx, -1, 0, targetTimestamp, targetTimestamp, 0);
            }

            // 刷新解码器缓冲
            if (_videoCodecCtx != null)
                ffmpeg.avcodec_flush_buffers(_videoCodecCtx);
            if (_audioCodecCtx != null)
                ffmpeg.avcodec_flush_buffers(_audioCodecCtx);

            // 清空音频缓冲
            _waveProvider?.ClearBuffer();
            _audioBytesWritten = 0;
            _audioClockBaseMs = positionMs;
            _audioStopwatch.Restart();
            if (_isPaused) _audioStopwatch.Stop();

            lock (_positionLock)
            {
                _currentPositionMs = positionMs;
            }
            PositionChanged?.Invoke(positionMs);
        }
    }

    public void Close()
    {
        Stop();
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

    #region 私有方法 - 解码器

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
                lock (_seekLock)
                {
                    readRet = ffmpeg.av_read_frame(_fmtCtx, packet);
                }

                if (readRet < 0)
                {
                    // EOF 或错误
                    if (readRet == ffmpeg.AVERROR_EOF)
                    {
                        // 刷新解码器
                        FlushDecoder(packet, frame, true);
                        // 等待音频播放完
                        DrainAudio();
                        PlaybackEnded?.Invoke();
                    }
                    break;
                }

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

            // 计算帧 PTS（毫秒）
            long ptsMs = GetFramePtsMs(frame, _videoStreamIndex);
            if (ptsMs >= 0)
            {
                // 音视频同步：等待音频时钟
                SyncToAudio(ptsMs);

                lock (_positionLock)
                {
                    _currentPositionMs = ptsMs;
                }
                PositionChanged?.Invoke(ptsMs);
            }

            // 转换并显示帧
            Bitmap? bmp = ConvertFrameToBitmap(frame);
            if (bmp != null)
            {
                FrameDecoded?.Invoke(bmp);
            }

            ffmpeg.av_frame_unref(frame);
        }
    }

    private void DecodeAudioFrame(AVPacket* packet, AVFrame* frame)
    {
        if (_audioCodecCtx == null || _swrCtx == null || _waveProvider == null) return;

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

    private void FlushDecoder(AVPacket* packet, AVFrame* frame, bool isVideo)
    {
        // 向解码器发送 NULL packet 以刷新
        ffmpeg.avcodec_send_packet(_videoCodecCtx, null);
        while (ffmpeg.avcodec_receive_frame(_videoCodecCtx, frame) >= 0)
        {
            long ptsMs = GetFramePtsMs(frame, _videoStreamIndex);
            if (ptsMs >= 0)
            {
                lock (_positionLock) { _currentPositionMs = ptsMs; }
                PositionChanged?.Invoke(ptsMs);
            }
            Bitmap? bmp = ConvertFrameToBitmap(frame);
            if (bmp != null) FrameDecoded?.Invoke(bmp);
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
    /// 获取当前音频时钟（毫秒）。
    /// </summary>
    private long GetAudioClockMs()
    {
        if (_waveProvider == null)
        {
            // 无音频时用当前位置
            return GetCurrentPositionMs();
        }

        double bytesPerSec = OutSampleRate * OutChannels * 2;
        long bufferedBytes = _waveProvider.BufferedBytes;

        long playedBytes;
        lock (_audioLock)
        {
            playedBytes = _audioBytesWritten - bufferedBytes;
        }

        long clockFromBytes = (long)(playedBytes / bytesPerSec * 1000.0);

        // 结合基准和已播放量
        return _audioClockBaseMs + clockFromBytes;
    }

    private long GetCurrentPositionMs()
    {
        lock (_positionLock) { return _currentPositionMs; }
    }

    private Bitmap? ConvertFrameToBitmap(AVFrame* frame)
    {
        if (_swsCtx == null) return null;

        int stride = _videoWidth * 3; // RGB24
        byte[] rgbBuffer = new byte[stride * _videoHeight];

        fixed (byte* rgbPtr = rgbBuffer)
        {
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
            byte*[] dstData = { rgbPtr };
            int[] dstStride = { stride };

            ffmpeg.sws_scale(_swsCtx,
                srcData, srcStride, 0, _videoHeight,
                dstData, dstStride);
        }

        Bitmap bmp = new(_videoWidth, _videoHeight, PixelFormat.Format24bppRgb);
        Rectangle rect = new(0, 0, _videoWidth, _videoHeight);
        BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            Marshal.Copy(rgbBuffer, 0, bmpData.Scan0, rgbBuffer.Length);
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }
        return bmp;
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

        if (_swrCtx != null) { SwrContext* swr = _swrCtx; ffmpeg.swr_free(&swr); _swrCtx = null; }
        if (_swsCtx != null) { ffmpeg.sws_freeContext(_swsCtx); _swsCtx = null; }
        if (_videoCodecCtx != null) { AVCodecContext* vc = _videoCodecCtx; ffmpeg.avcodec_free_context(&vc); _videoCodecCtx = null; }
        if (_audioCodecCtx != null) { AVCodecContext* ac = _audioCodecCtx; ffmpeg.avcodec_free_context(&ac); _audioCodecCtx = null; }
        if (_fmtCtx != null) { AVFormatContext* fc = _fmtCtx; ffmpeg.avformat_close_input(&fc); _fmtCtx = null; }

        _videoStreamIndex = -1;
        _audioStreamIndex = -1;
        _audioBytesWritten = 0;
        _audioClockBaseMs = 0;
        _audioStopwatch.Reset();
    }

    #endregion
}
