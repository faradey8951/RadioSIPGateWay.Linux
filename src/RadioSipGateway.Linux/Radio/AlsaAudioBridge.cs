using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using RadioSipGateway.Linux.Configuration;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RadioSipGateway.Linux.Radio;

/// <summary>
/// First-pass Linux ALSA/RTP bridge. ALSA is accessed via arecord/aplay so no native
/// ALSA bindings are required. Audio is mono signed 16-bit PCM at 8 kHz (20 ms frames).
/// This class deliberately does not control PTT.
/// </summary>
internal sealed class AlsaAudioBridge : IAsyncDisposable
{
    private const int SampleRate = 8000;
    private const int SamplesPerFrame = 160;
    private const int BytesPerFrame = SamplesPerFrame * sizeof(short);

    private readonly AudioConfig _config;
    private readonly Action<string> _log;
    private readonly AudioEncoder _decoder = new();
    private readonly Channel<short[]> _playbackQueue = Channel.CreateBounded<short[]>(
        new BoundedChannelOptions(25) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private Process? _capture;
    private Process? _playback;
    private Task? _captureTask;
    private Task? _playbackTask;
    private AudioFormat? _negotiatedFormat;
    private VoIPMediaSession? _media;
    private int _started;

    public AlsaAudioBridge(AudioConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public void Attach(VoIPMediaSession media)
    {
        _media = media;
        // Keep the first Linux implementation on the widely supported 8 kHz G.711 codecs.
        media.AudioExtrasSource.RestrictFormats(f => f.Codec is AudioCodecsEnum.PCMU or AudioCodecsEnum.PCMA);
        media.AudioExtrasSource.SetSource(AudioSourcesEnum.None);
        media.OnAudioFormatsNegotiated += formats =>
        {
            _negotiatedFormat = formats.FirstOrDefault(f => f.Codec is AudioCodecsEnum.PCMU or AudioCodecsEnum.PCMA);
            if (_negotiatedFormat.HasValue)
                _log($"ALSA/RTP: согласован аудиокодек {_negotiatedFormat.Value.Codec}, формат {_negotiatedFormat.Value.FormatID}.");
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
                short[] pcm = _decoder.DecodeAudio(packet.Payload, format.Value);
                if (pcm.Length > 0) _playbackQueue.Writer.TryWrite(pcm);
            }
            catch (Exception ex)
            {
                _log($"ALSA/RTP: ошибка декодирования входящего аудио: {ex.Message}");
            }
        };
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
            _log($"ALSA: аудиомост запущен; capture={_config.CaptureDevice}, playback={_config.PlaybackDevice}, PCM mono S16_LE {SampleRate} Hz. PTT не задействован.");
            return Task.CompletedTask;
        }
        catch
        {
            _stop.Cancel();
            StopProcess(_capture);
            StopProcess(_playback);
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

                _media?.AudioExtrasSource.ExternalAudioSourceRawSample(
                    AudioSamplingRatesEnum.Rate8KHz, 20, samples);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested) _log($"ALSA: ошибка захвата звука (arecord): {ex.Message}");
        }
    }

    private async Task PlaybackLoopAsync()
    {
        try
        {
            Stream input = _playback!.StandardInput.BaseStream;
            await foreach (short[] samples in _playbackQueue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                byte[] bytes = new byte[samples.Length * sizeof(short)];
                for (int i = 0; i < samples.Length; i++)
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
                await input.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested) _log($"ALSA: ошибка воспроизведения звука (aplay): {ex.Message}");
        }
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
        StopProcess(_capture);
        StopProcess(_playback);
        foreach (Task? task in new[] { _captureTask, _playbackTask })
        {
            if (task == null) continue;
            try { await task.ConfigureAwait(false); } catch { }
        }
        _stop.Dispose();
        _log("ALSA: аудиомост остановлен; PTT не задействован.");
    }
}
