using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using RadioSipGateway.Linux.Configuration;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RadioSipGateway.Linux.Radio;

/// <summary>
/// Linux ALSA/RTP bridge. The current SIP track is G.711 narrowband mono PCM
/// at 8 kHz. A persistent 200..3400 Hz Butterworth filter is applied in each
/// direction; the upper edge is limited by the 8 kHz RTP codec's Nyquist band.
/// Playback writes timed silence when RTP is absent to keep ALSA's PCM clock fed.
/// </summary>
internal sealed class AlsaAudioBridge : IAsyncDisposable
{
    private const int SampleRate = 8000;
    private const int SamplesPerFrame = 160;
    private const int BytesPerFrame = SamplesPerFrame * sizeof(short);
    private static readonly TimeSpan FramePeriod = TimeSpan.FromMilliseconds(20);

    private readonly AudioConfig _config;
    private readonly TransmitConfig _transmitConfig;
    private readonly VoxConfig _voxConfig;
    private readonly RadioConfig _radioConfig;
    private readonly Cm108PttController? _ptt;
    private readonly Action<string> _log;
    private readonly AudioEncoder _encoder = new();
    private readonly SipRadioCompressor _compressor = new();
    private readonly AudioBandPassFilter _radioToSipFilter = new(200.0, 3400.0);
    private readonly AudioBandPassFilter _sipToRadioFilter = new(200.0, 3400.0);
    private readonly DtmfDecoder _radioDtmfDecoder = new(SampleRate);
    private readonly Channel<short[]> _playbackQueue = Channel.CreateBounded<short[]>(
        new BoundedChannelOptions(25) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private Process? _capture;
    private Process? _playback;
    private Task? _captureTask;
    private Task? _playbackTask;
    private AudioFormat? _negotiatedFormat;
    private VoIPMediaSession? _media;
    private DateTimeOffset? _voxAboveSince;
    private int _controlToneActive;
    private int _started;

    public AlsaAudioBridge(AudioConfig config, TransmitConfig transmitConfig, VoxConfig voxConfig,
        RadioConfig radioConfig, Cm108PttController? ptt, Action<string> log)
    {
        _config = config;
        _transmitConfig = transmitConfig;
        _voxConfig = voxConfig;
        _radioConfig = radioConfig;
        _ptt = ptt;
        _log = log;
        _compressor.SpeedMilliseconds = _radioConfig.SipRadioCompressorSpeedMs;

        _radioDtmfDecoder.DigitDetected += digit => _log($"RADIO: обнаружен DTMF {digit}.");
        _radioDtmfDecoder.ToneActivityChanged += active => Volatile.Write(ref _controlToneActive, active ? 1 : 0);
        _radioDtmfDecoder.RepeaterTone1750Detected += () => _log("RADIO: обнаружен тон открытия репитера 1750 Гц (логическая команда DTMF '*').");
    }

    public void Attach(VoIPMediaSession media)
    {
        _media = media;
        media.AudioExtrasSource.RestrictFormats(f => f.Codec is AudioCodecsEnum.PCMU or AudioCodecsEnum.PCMA);
        media.AudioExtrasSource.SetSource(AudioSourcesEnum.None);
        media.OnAudioFormatsNegotiated += formats =>
        {
            _negotiatedFormat = formats.FirstOrDefault(f => f.Codec is AudioCodecsEnum.PCMU or AudioCodecsEnum.PCMA);
            if (_negotiatedFormat.HasValue)
            {
                media.AudioExtrasSource.SetAudioSourceFormat(_negotiatedFormat.Value);
                _log($"ALSA/RTP: согласован аудиокодек {_negotiatedFormat.Value.Codec}, формат {_negotiatedFormat.Value.FormatID}.");
            }
            else
                _log("ALSA/RTP: предупреждение — не найден согласованный кодек PCMU/PCMA.");
        };

        media.OnRtpPacketReceived += (remote, mediaType, packet) =>
        {
            if (mediaType != SDPMediaTypesEnum.audio || _stop.IsCancellationRequested) return;
            try
            {
                AudioFormat? format = _negotiatedFormat;
                if (!format.HasValue) return;
                short[] pcm = _encoder.DecodeAudio(packet.Payload, format.Value);
                if (pcm.Length == 0) return;

                bool speechActive = IsRemoteAudioActive(pcm);
                short[] conditioned = _sipToRadioFilter.Process(pcm, SampleRate) ?? Array.Empty<short>();
                _compressor.Process(conditioned, speechActive, _radioConfig.SipPlaybackGainDb,
                    _radioConfig.SipRadioCompressorEnabled, SampleRate);
                _playbackQueue.Writer.TryWrite(FitPlaybackFrame(conditioned));

                if (_ptt != null && speechActive)
                    _ptt.NotifyRemoteAudio();
            }
            catch (Exception ex)
            {
                _log($"ALSA/RTP: ошибка декодирования/обработки входящего аудио: {ex.GetType().Name}: {ex.Message}");
                _ptt?.ReleaseTransmit("ошибка обработки входящего RTP-аудио");
            }
        };
    }

    private bool IsRemoteAudioActive(short[] samples)
    {
        if (!_transmitConfig.VoxEnabled)
        {
            _voxAboveSince = DateTimeOffset.UtcNow;
            return true;
        }
        if (samples.Length == 0) return false;

        double sum = 0;
        foreach (short sample in samples)
        {
            double normalized = sample / 32768.0;
            sum += normalized * normalized;
        }
        double rms = Math.Sqrt(sum / samples.Length);
        double dbfs = rms <= 0 ? -120.0 : 20.0 * Math.Log10(rms);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (dbfs >= _voxConfig.ThresholdDbfs)
        {
            _voxAboveSince ??= now;
            return now - _voxAboveSince.Value >= TimeSpan.FromMilliseconds(_voxConfig.ActivationDelayMs);
        }

        _voxAboveSince = null;
        return false;
    }

    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        try
        {
            _playback = StartAlsaProcess("aplay", _config.PlaybackDevice,
                "-q", "-D", _config.PlaybackDevice, "-t", "raw", "-f", "S16_LE", "-r", SampleRate.ToString(), "-c", "1");
            _capture = StartAlsaProcess("arecord", _config.CaptureDevice,
                "-q", "-D", _config.CaptureDevice, "-t", "raw", "-f", "S16_LE", "-r", SampleRate.ToString(), "-c", "1");

            _playbackTask = Task.Run(PlaybackLoopAsync);
            _captureTask = Task.Run(CaptureLoopAsync);
            _log($"ALSA: аудиомост запущен; capture={_config.CaptureDevice}, playback={_config.PlaybackDevice}, PCM mono S16_LE {SampleRate} Hz; фильтр 200–3400 Hz; компрессор SIP→радио={(_radioConfig.SipRadioCompressorEnabled ? "включён" : "выключен")}; PTT={(_ptt == null ? "выключен" : "CM108 GPIO3/COS VOLDN")}.");
            return Task.CompletedTask;
        }
        catch
        {
            _stop.Cancel();
            StopProcess(_capture);
            StopProcess(_playback);
            _ptt?.ReleaseTransmit("ошибка запуска ALSA-моста");
            throw;
        }
    }

    private static Process StartAlsaProcess(string executable, string device, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = executable == "aplay",
            RedirectStandardOutput = executable == "arecord",
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"Не удалось запустить {executable} для ALSA-устройства {device}.");
        return process;
    }

    private async Task CaptureLoopAsync()
    {
        try
        {
            Stream output = _capture!.StandardOutput.BaseStream;
            byte[] bytes = new byte[BytesPerFrame];
            byte[] filteredBytes = new byte[BytesPerFrame];
            while (!_stop.IsCancellationRequested)
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = await output.ReadAsync(bytes.AsMemory(offset, bytes.Length - offset), _stop.Token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("arecord завершил поток захвата.");
                    offset += read;
                }

                var samples = new short[SamplesPerFrame];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2));

                short[] conditioned = _radioToSipFilter.Process(samples, SampleRate) ?? Array.Empty<short>();
                for (int i = 0; i < conditioned.Length; i++)
                    BinaryPrimitives.WriteInt16LittleEndian(filteredBytes.AsSpan(i * 2, 2), conditioned[i]);

                // The Windows Stage 42 decoder re-arms the same DTMF digit after two
                // 20 ms frames with no valid pair (not only after absolute digital silence).
                _radioDtmfDecoder.ProcessBuffer(filteredBytes, 0, filteredBytes.Length);

                // Confirmed DTMF/1750 control tones are not leaked into the radio->SIP speech track.
                if (Volatile.Read(ref _controlToneActive) != 0)
                    Array.Clear(conditioned, 0, conditioned.Length);

                AudioFormat? format = _negotiatedFormat;
                VoIPMediaSession? media = _media;
                if (!format.HasValue || media == null) continue;

                byte[] encoded = _encoder.EncodeAudio(conditioned, format.Value);
                if (encoded.Length > 0)
                    media.SendAudio((uint)encoded.Length, encoded);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
            {
                _log($"ALSA/RTP: ошибка захвата/фильтрации/кодирования: {ex.GetType().Name}: {ex.Message}");
                _ptt?.ReleaseTransmit("ошибка захвата ALSA");
            }
        }
    }

    private async Task PlaybackLoopAsync()
    {
        try
        {
            Stream input = _playback!.StandardInput.BaseStream;
            byte[] bytes = new byte[BytesPerFrame];
            short[] silence = new short[SamplesPerFrame];
            using var timer = new PeriodicTimer(FramePeriod);

            // A continuous 20 ms PCM clock prevents aplay underruns during remote RTP
            // silence/DTX gaps. If no packet is queued, feed an explicit zero frame.
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                short[] frame;
                if (_playbackQueue.Reader.TryRead(out short[]? queued) && queued != null)
                    frame = FitPlaybackFrame(queued);
                else
                    frame = silence;
                for (int i = 0; i < SamplesPerFrame; i++)
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), frame[i]);
                await input.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
            {
                _log($"ALSA: ошибка воспроизведения звука (aplay): {ex.GetType().Name}: {ex.Message}");
                _ptt?.ReleaseTransmit("ошибка воспроизведения ALSA");
            }
        }
    }

    private static short[] FitPlaybackFrame(short[] samples)
    {
        if (samples.Length == SamplesPerFrame) return samples;
        var fitted = new short[SamplesPerFrame];
        if (samples.Length == 0) return fitted;
        if (samples.Length < SamplesPerFrame)
        {
            Array.Copy(samples, fitted, samples.Length);
            return fitted;
        }
        Array.Copy(samples, fitted, SamplesPerFrame);
        return fitted;
    }

    private static void StopProcess(Process? process)
    {
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { process.Dispose(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _playbackQueue.Writer.TryComplete();
        _ptt?.ReleaseTransmit("ALSA/RTP-мост останавливается");
        StopProcess(_capture);
        StopProcess(_playback);
        foreach (Task? task in new[] { _captureTask, _playbackTask })
        {
            if (task == null) continue;
            try { await task.ConfigureAwait(false); } catch { }
        }
        _stop.Dispose();
        _log("ALSA: аудиомост остановлен.");
    }
}
