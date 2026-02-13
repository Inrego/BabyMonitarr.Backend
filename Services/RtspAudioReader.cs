using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.Extensions.Logging;
using BabyMonitarr.Backend.Models;
using FFmpeg.AutoGen;

namespace BabyMonitarr.Backend.Services
{
    public class AudioFormatEventArgs : EventArgs
    {
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
        public int BytesPerSample { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public bool IsPlanar { get; set; }
        public AVSampleFormat SampleFormat { get; set; }
    }

    internal class RtspAudioReader : IDisposable
    {
        private readonly AudioSettings _settings;
        private readonly ILogger _logger;
        private readonly string _rtspUrl;
        private readonly string _username;
        private readonly string _password;
        private bool _isDisposed = false;
        private Task? _processingTask;
        private CancellationTokenSource? _cts;
        private const int MaxRetryAttempts = 3;
        private const int RetryDelayMs = 5000;
        
        public event EventHandler<AudioFormatEventArgs>? AudioDataReceived;
        
        public RtspAudioReader(AudioSettings settings, ILogger logger)
        {
            _settings = settings;
            _logger = logger;
            _rtspUrl = _settings.CameraStreamUrl ?? string.Empty;
            _username = _settings.CameraUsername ?? string.Empty;
            _password = _settings.CameraPassword ?? string.Empty;
            
            InitializeFFmpeg();
        }
        
        private void InitializeFFmpeg()
        {
            _logger.LogInformation("Initializing FFmpeg for RTSP stream processing");
            
            try
            {
                // Calculate FFmpeg path relative to the application
                string appPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                string ffmpegPath = Path.Combine(appPath, "FFmpeg");
                
                if (!Directory.Exists(ffmpegPath))
                {
                    _logger.LogError($"FFmpeg directory not found at {ffmpegPath}. Please ensure FFmpeg binaries are included with the application.");
                    throw new DirectoryNotFoundException($"FFmpeg directory not found at {ffmpegPath}");
                }
                
                // Set FFmpeg library path to our bundled copy
                ffmpeg.RootPath = ffmpegPath;
                DynamicallyLoadedBindings.Initialize();

                // Configure FFmpeg logging
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);  // Only show warnings and errors
                
                // Log FFmpeg version info
                _logger.LogInformation($"FFmpeg version: {ffmpeg.av_version_info()}");
                _logger.LogInformation($"Using FFmpeg binaries from: {ffmpegPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize FFmpeg. Make sure FFmpeg binaries are included with the application.");
                throw;
            }
        }
        
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting RTSP audio stream reader: {0}", _rtspUrl);
            
            if (string.IsNullOrEmpty(_rtspUrl))
            {
                _logger.LogError("Cannot start RTSP stream without a URL");
                return Task.CompletedTask;
            }
            
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _processingTask = Task.Run(() => ProcessRtspStreamWithRetry(_cts.Token), _cts.Token);
            
            return Task.CompletedTask;
        }

        private async Task ProcessRtspStreamWithRetry(CancellationToken cancellationToken)
        {
            int retryCount = 0;
            bool success = false;

            while (!success && retryCount < MaxRetryAttempts && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (retryCount > 0)
                    {
                        _logger.LogInformation($"Retrying RTSP connection (attempt {retryCount + 1} of {MaxRetryAttempts})...");
                        await Task.Delay(RetryDelayMs, cancellationToken);
                    }

                    ProcessRtspStream(cancellationToken);
                    success = true;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    _logger.LogError(ex, $"Error processing RTSP stream (attempt {retryCount} of {MaxRetryAttempts})");
                    
                    if (retryCount >= MaxRetryAttempts)
                    {
                        _logger.LogError("Max retry attempts reached. Giving up on RTSP stream.");
                    }
                }
            }
        }
        
        private unsafe void ProcessRtspStream(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Connecting to RTSP stream: {0}", _rtspUrl);
            
            AVFormatContext* formatContext = null;
            AVCodecContext* codecContext = null;
            int audioStream = -1;
            
            try
            {
                // Initialize format context
                formatContext = ffmpeg.avformat_alloc_context();
                
                // Create a custom callback to filter log messages
                av_log_set_callback_callback logCallback = (p1, level, format, vl) =>
                {
                    // Only show errors (level 16) and above
                    if (level > ffmpeg.AV_LOG_ERROR)
                    {
                        return;
                    }

                    ffmpeg.av_log_default_callback(p1, level, format, vl);
                };
                ffmpeg.av_log_set_callback(logCallback);
                
                // Set up credentials and options
                AVDictionary* options = null;
                
                // Set stream options
                ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
                ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);      // Reduce buffering
                ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);      // Low delay mode
                ffmpeg.av_dict_set(&options, "max_delay", "100000", 0);     // 100ms max delay
                ffmpeg.av_dict_set(&options, "analyzeduration", "1000000", 0);  // 1 second analysis
                ffmpeg.av_dict_set(&options, "probesize", "100000", 0);     // Small probe size
                
                // Authentication if needed
                if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
                {
                    ffmpeg.av_dict_set(&options, "username", _username, 0);
                    ffmpeg.av_dict_set(&options, "password", _password, 0);
                }
                
                // Open input
                int ret = ffmpeg.avformat_open_input(&formatContext, _rtspUrl, null, &options);
                if (ret < 0)
                {
                    byte* errorBuf = stackalloc byte[1024];
                    ffmpeg.av_strerror(ret, errorBuf, 1024);
                    throw new Exception($"Could not open RTSP stream: {Marshal.PtrToStringAnsi((IntPtr)errorBuf)}");
                }
                
                // Get stream information
                ret = ffmpeg.avformat_find_stream_info(formatContext, null);
                if (ret < 0)
                {
                    throw new Exception("Could not find stream information");
                }
                
                // Find audio stream
                for (int i = 0; i < formatContext->nb_streams; i++)
                {
                    if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        audioStream = i;
                        break;
                    }
                }
                
                if (audioStream == -1)
                {
                    throw new Exception("Could not find audio stream in RTSP feed");
                }
                
                // Get codec
                AVCodecParameters* codecParams = formatContext->streams[audioStream]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
                if (codec == null)
                {
                    throw new Exception("Unsupported audio codec");
                }
                
                // Initialize codec context
                codecContext = ffmpeg.avcodec_alloc_context3(codec);
                ret = ffmpeg.avcodec_parameters_to_context(codecContext, codecParams);
                if (ret < 0)
                {
                    throw new Exception("Failed to copy codec parameters to context");
                }
                
                // Configure codec to be more tolerant of errors
                codecContext->err_recognition = 0;  // Be more tolerant of errors
                codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;  // Use fast decoding mode
                
                // Open codec
                ret = ffmpeg.avcodec_open2(codecContext, codec, null);
                if (ret < 0)
                {
                    throw new Exception("Failed to open codec");
                }
                
                int channelCount = codecContext->ch_layout.nb_channels;
                _logger.LogInformation($"Audio stream found: {codecContext->sample_fmt} format, {codecContext->sample_rate}Hz, {channelCount} channels");
                
                // Read packets and decode audio
                AVPacket* packet = ffmpeg.av_packet_alloc();
                AVFrame* frame = ffmpeg.av_frame_alloc();
                
                while (!cancellationToken.IsCancellationRequested)
                {
                    ret = ffmpeg.av_read_frame(formatContext, packet);
                    if (ret < 0)
                    {
                        if (ret == ffmpeg.AVERROR_EOF)
                        {
                            _logger.LogInformation("End of stream reached");
                        }
                        else
                        {
                            byte* errorBuf = stackalloc byte[1024];
                            ffmpeg.av_strerror(ret, errorBuf, 1024);
                            _logger.LogWarning($"Error reading frame: {Marshal.PtrToStringAnsi((IntPtr)errorBuf)}");
                        }
                        break;
                    }
                    
                    // Skip non-audio packets immediately
                    if (packet->stream_index != audioStream)
                    {
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }
                    
                    // Send packet to decoder
                    ret = ffmpeg.avcodec_send_packet(codecContext, packet);
                    ffmpeg.av_packet_unref(packet);
                    
                    if (ret < 0)
                    {
                        continue;
                    }
                    
                    // Get decoded audio frames
                    while (true)
                    {
                        ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        {
                            break;
                        }
                        else if (ret < 0)
                        {
                            _logger.LogWarning("Error receiving audio frame");
                            break;
                        }
                        
                        ProcessAudioFrame(frame, codecContext);
                        ffmpeg.av_frame_unref(frame);
                    }
                }
                
                // Flush the decoder
                ffmpeg.avcodec_send_packet(codecContext, null);
                while (ret >= 0)
                {
                    ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                    if (ret >= 0)
                    {
                        ProcessAudioFrame(frame, codecContext);
                        ffmpeg.av_frame_unref(frame);
                    }
                }
                
                // Clean up
                ffmpeg.av_frame_free(&frame);
                ffmpeg.av_packet_free(&packet);
            }
            finally
            {
                if (codecContext != null)
                {
                    ffmpeg.avcodec_free_context(&codecContext);
                }
                
                if (formatContext != null)
                {
                    ffmpeg.avformat_close_input(&formatContext);
                    ffmpeg.avformat_free_context(formatContext);
                }
            }
        }
        
        private unsafe void ProcessAudioFrame(AVFrame* frame, AVCodecContext* codecContext)
        {
            try
            {
                int channelCount = codecContext->ch_layout.nb_channels;
                int bytesPerSample = ffmpeg.av_get_bytes_per_sample(codecContext->sample_fmt);
                bool isPlanar = ffmpeg.av_sample_fmt_is_planar(codecContext->sample_fmt) != 0;
                
                // Calculate total data size needed
                int dataSize = bytesPerSample * frame->nb_samples * channelCount;
                
                if (dataSize <= 0)
                {
                    _logger.LogWarning("Invalid audio frame size");
                    return;
                }

                byte[] audioData = new byte[dataSize];
                
                if (isPlanar)
                {
                    // Planar format: each channel is in a separate buffer
                    int samplesPerChannel = frame->nb_samples * bytesPerSample;
                    
                    for (int ch = 0; ch < channelCount; ch++)
                    {
                        IntPtr sourcePtr = (IntPtr)frame->extended_data[ch];
                        
                        for (int sample = 0; sample < frame->nb_samples; sample++)
                        {
                            int sourceOffset = sample * bytesPerSample;
                            int targetOffset = (sample * channelCount + ch) * bytesPerSample;
                            
                            // Copy each sample for this channel
                            Marshal.Copy(sourcePtr + sourceOffset, audioData, targetOffset, bytesPerSample);
                        }
                    }
                }
                else
                {
                    // Interleaved format: all channels are in one buffer
                    IntPtr sourcePtr = (IntPtr)frame->extended_data[0];
                    Marshal.Copy(sourcePtr, audioData, 0, dataSize);
                }
                
                // Apply volume adjustment to the audio data
                audioData = AdjustVolume(audioData, bytesPerSample, _settings.VolumeAdjustmentDb);
                
                // Create event args with audio format information
                var eventArgs = new AudioFormatEventArgs
                {
                    AudioData = audioData,
                    BytesPerSample = bytesPerSample,
                    SampleRate = codecContext->sample_rate,
                    Channels = channelCount,
                    IsPlanar = isPlanar,
                    SampleFormat = codecContext->sample_fmt
                };
                
                // Log audio format details for debugging
                _logger.LogDebug($"Audio frame: format={codecContext->sample_fmt}, " +
                               $"rate={codecContext->sample_rate}Hz, " +
                               $"channels={channelCount}, " +
                               $"bytesPerSample={bytesPerSample}, " +
                               $"isPlanar={isPlanar}, " +
                               $"dataSize={dataSize}");
                
                // Raise event with the audio data and format information
                AudioDataReceived?.Invoke(this, eventArgs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing audio frame");
            }
        }
        
        /// <summary>
        /// Adjusts the volume of audio samples by the specified decibel value
        /// </summary>
        /// <param name="audioData">Raw audio data</param>
        /// <param name="bytesPerSample">Number of bytes per audio sample</param>
        /// <param name="dbAdjustment">Volume adjustment in decibels</param>
        /// <returns>Volume-adjusted audio data</returns>
        private byte[] AdjustVolume(byte[] audioData, int bytesPerSample, double dbAdjustment)
        {
            // No adjustment needed
            if (Math.Abs(dbAdjustment) < 0.01)
            {
                return audioData;
            }
            
            // Convert dB to amplitude multiplier
            // dB = 20 * log10(amplitudeRatio)
            // So amplitudeRatio = 10^(dB/20)
            double amplitudeMultiplier = Math.Pow(10, dbAdjustment / 20.0);
            
            // Log the adjustment being applied
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug($"Applying volume adjustment: {dbAdjustment:F2} dB (multiplier: {amplitudeMultiplier:F4})");
            }
            
            byte[] adjustedData = new byte[audioData.Length];
            
            // Process based on sample format
            switch (bytesPerSample)
            {
                case 2: // 16-bit PCM
                    Adjust16BitSamples(audioData, adjustedData, amplitudeMultiplier);
                    break;
                    
                case 4: // 32-bit float or int
                    Adjust32BitSamples(audioData, adjustedData, amplitudeMultiplier);
                    break;
                    
                case 8: // 64-bit double or long
                    Adjust64BitSamples(audioData, adjustedData, amplitudeMultiplier);
                    break;
                    
                default:
                    _logger.LogWarning($"Unsupported bytes per sample for volume adjustment: {bytesPerSample}");
                    return audioData; // Return original if format not supported
            }
            
            return adjustedData;
        }
        
        private void Adjust16BitSamples(byte[] source, byte[] target, double multiplier)
        {
            for (int i = 0; i < source.Length; i += 2)
            {
                // Convert bytes to 16-bit short
                short sample = BitConverter.ToInt16(source, i);
                
                // Apply volume adjustment
                double adjusted = sample * multiplier;
                
                // Clip to prevent overflow
                if (adjusted > short.MaxValue) adjusted = short.MaxValue;
                if (adjusted < short.MinValue) adjusted = short.MinValue;
                
                // Convert back to bytes
                byte[] bytes = BitConverter.GetBytes((short)adjusted);
                target[i] = bytes[0];
                target[i + 1] = bytes[1];
            }
        }
        
        private void Adjust32BitSamples(byte[] source, byte[] target, double multiplier)
        {
            for (int i = 0; i < source.Length; i += 4)
            {
                // Try to determine if this is float or int format
                // This is a simplification; in practice you might need to check the actual format
                float floatSample = BitConverter.ToSingle(source, i);
                
                // If the float value seems valid (between -1 and 1), treat as float
                if (Math.Abs(floatSample) <= 1.0)
                {
                    // Apply volume adjustment to float
                    float adjusted = (float)(floatSample * multiplier);
                    
                    // Clip to prevent overflow for float values (-1.0 to 1.0)
                    if (adjusted > 1.0f) adjusted = 1.0f;
                    if (adjusted < -1.0f) adjusted = -1.0f;
                    
                    // Convert back to bytes
                    byte[] bytes = BitConverter.GetBytes(adjusted);
                    Buffer.BlockCopy(bytes, 0, target, i, 4);
                }
                else
                {
                    // Treat as 32-bit integer
                    int intSample = BitConverter.ToInt32(source, i);
                    
                    // Apply volume adjustment
                    double adjusted = intSample * multiplier;
                    
                    // Clip to prevent overflow
                    if (adjusted > int.MaxValue) adjusted = int.MaxValue;
                    if (adjusted < int.MinValue) adjusted = int.MinValue;
                    
                    // Convert back to bytes
                    byte[] bytes = BitConverter.GetBytes((int)adjusted);
                    Buffer.BlockCopy(bytes, 0, target, i, 4);
                }
            }
        }
        
        private void Adjust64BitSamples(byte[] source, byte[] target, double multiplier)
        {
            for (int i = 0; i < source.Length; i += 8)
            {
                // Try to determine if this is double or long format
                double doubleSample = BitConverter.ToDouble(source, i);
                
                // If the double value seems valid (typically audio is normalized between -1 and 1)
                if (Math.Abs(doubleSample) <= 1.0)
                {
                    // Apply volume adjustment to double
                    double adjusted = doubleSample * multiplier;
                    
                    // Clip to prevent overflow for float values (-1.0 to 1.0)
                    if (adjusted > 1.0) adjusted = 1.0;
                    if (adjusted < -1.0) adjusted = -1.0;
                    
                    // Convert back to bytes
                    byte[] bytes = BitConverter.GetBytes(adjusted);
                    Buffer.BlockCopy(bytes, 0, target, i, 8);
                }
                else
                {
                    // Treat as 64-bit integer
                    long longSample = BitConverter.ToInt64(source, i);
                    
                    // Apply volume adjustment
                    double adjusted = longSample * multiplier;
                    
                    // Clip to prevent overflow
                    if (adjusted > long.MaxValue) adjusted = long.MaxValue;
                    if (adjusted < long.MinValue) adjusted = long.MinValue;
                    
                    // Convert back to bytes
                    byte[] bytes = BitConverter.GetBytes((long)adjusted);
                    Buffer.BlockCopy(bytes, 0, target, i, 8);
                }
            }
        }
        
        public void Stop()
        {
            _cts?.Cancel();
            
            if (_processingTask != null)
            {
                try
                {
                    _processingTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // Task was cancelled, expected
                }
                _processingTask = null;
            }
            
            _cts?.Dispose();
            _cts = null;
            
            _logger.LogInformation("RTSP audio reader stopped");
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    Stop();
                }
                
                _isDisposed = true;
            }
        }
        
        ~RtspAudioReader()
        {
            Dispose(false);
        }
    }
}