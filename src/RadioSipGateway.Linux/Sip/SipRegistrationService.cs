using System.Net;
using System.Threading.Channels;
using RadioSipGateway.Linux.Configuration;
using RadioSipGateway.Linux.Radio;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace RadioSipGateway.Linux.Sip;

/// <summary>
/// SIP registration plus a controlled incoming-call/RTP negotiation test.
/// The test media session is send-only and is NOT the final ALSA radio bridge.
/// </summary>
internal sealed class SipRegistrationService : IAsyncDisposable
{
    private readonly SipConfig _config;
    private readonly AudioConfig _audioConfig;
    private readonly Action<string> _log;
    private readonly Channel<byte> _retryRequests = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _registrationAgent;
    private SIPUserAgent? _callUserAgent;
    private VoIPMediaSession? _activeTestMedia;
    private AlsaAudioBridge? _activeAudioBridge;
    private Task? _retryWorker;
    private int _retryAttempt;
    private int _retryQueued;
    private int _callInProgress;
    private volatile bool _registered;
    private volatile bool _stopping;

    public SipRegistrationService(SipConfig config, AudioConfig audioConfig, Action<string> log)
    {
        _config = config;
        _audioConfig = audioConfig;
        _log = log;
    }

    public bool IsRegistered => _registered;

    public void Start()
    {
        if (_transport != null) throw new InvalidOperationException("SIP service уже запущен.");
        if (string.IsNullOrWhiteSpace(_config.Username))
            throw new InvalidOperationException("Для SIP-регистрации необходимо задать SIP.Username в конфиге.");
        if (string.IsNullOrWhiteSpace(_config.Password) || _config.Password == "CHANGE_ME")
            throw new InvalidOperationException("Задайте реальный SIP.Password в локальном конфиге (не публикуйте его в GitHub).");

        var transport = new SIPTransport();
        try
        {
            transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, _config.LocalPort)));
            _transport = transport;

            // SIPSorcery's incoming-call event is Action-based; async work is launched explicitly.
            _callUserAgent = new SIPUserAgent(transport, null, true);
            _callUserAgent.OnIncomingCall += (ua, request) => _ = HandleIncomingCallAsync(ua, request);
            _callUserAgent.OnCallHungup += dialogue => HandleCallHungup(dialogue.CallId);

            _retryWorker = Task.Run(RetryWorkerAsync);
            StartRegistrationAgent();
            _log($"SIP: UDP transport слушает порт {_config.LocalPort}; регистрация на {_config.Server}:{_config.Port} запущена.");
            _log(!_config.AutoAnswerForRtpTest
                ? "SIP: входящие INVITE обнаруживаются; тестовый автоответ выключен, вызовы отклоняются с 503."
                : _audioConfig.BridgeEnabled
                    ? "SIP: тестовый автоответ включён; при вызове запускается двусторонний ALSA/RTP мост, PTT отключён."
                    : "SIP: тестовый автоответ включён; используется RTP-медиасессия с тишиной, без ALSA/PTT.");
        }
        catch
        {
            try { transport.Shutdown(); } catch { }
            _transport = null;
            throw;
        }
    }

    private void StartRegistrationAgent()
    {
        if (_stopping || _transport == null) return;

        string registrar = $"{_config.Server.Trim()}:{_config.Port}";
        var agent = new SIPRegistrationUserAgent(_transport, _config.Username, _config.Password, registrar, _config.RegistrationExpirySeconds);
        _registrationAgent = agent;

        agent.RegistrationSuccessful += (uri, response) =>
        {
            if (!ReferenceEquals(_registrationAgent, agent) || _stopping) return;
            _registered = true;
            _retryAttempt = 0;
            Interlocked.Exchange(ref _retryQueued, 0);
            _log($"SIP: регистрация успешна: {uri}; срок регистрации запрошен {_config.RegistrationExpirySeconds} с.");
        };

        agent.RegistrationFailed += (uri, response, error) =>
        {
            if (!ReferenceEquals(_registrationAgent, agent) || _stopping) return;
            _registered = false;
            string detail = response == null
                ? error
                : $"{response.StatusCode} {response.ReasonPhrase}" + (string.IsNullOrWhiteSpace(error) ? "" : $" — {error}");
            if (response?.Status == SIPResponseStatusCodesEnum.Unauthorised ||
                response?.Status == SIPResponseStatusCodesEnum.ProxyAuthenticationRequired)
                _log($"SIP: сервер отклонил аутентификацию ({detail}). Проверьте SIP.Username, SIP.Password и настройки учётной записи в Yate.");
            else
                _log($"SIP: ошибка регистрации: {detail}");
            QueueRetry();
        };

        agent.RegistrationTemporaryFailure += (uri, response, message) =>
        {
            if (!ReferenceEquals(_registrationAgent, agent) || _stopping) return;
            string status = response == null ? string.Empty : $" ({response.StatusCode} {response.ReasonPhrase})";
            _log($"SIP: временная ошибка регистрации{status}: {message}. Агент продолжит обработку; при окончательном отказе будет запущен повтор.");
        };

        agent.RegistrationRemoved += (uri, response) =>
        {
            if (!ReferenceEquals(_registrationAgent, agent) || _stopping) return;
            _registered = false;
            _log("SIP: регистрация снята сервером или истекла; будет выполнена повторная регистрация.");
            QueueRetry();
        };

        agent.Start();
    }

    private async Task HandleIncomingCallAsync(SIPUserAgent userAgent, SIPRequest request)
    {
        if (request == null || _stopping || _transport == null) return;

        string caller = ReadUser(request.Header?.From?.FromURI?.User, "неизвестный абонент");
        string requestTarget = ReadUser(request.URI?.User, "неизвестный адрес");
        string toTarget = ReadUser(request.Header?.To?.ToURI?.User, "не указан");
        string targetForLog = requestTarget != "неизвестный адрес" ? requestTarget : toTarget;
        _log($"SIP: получен INVITE: From={caller}; Request-URI={requestTarget}; To={toTarget}.");

        if (!ReferenceEquals(userAgent, _callUserAgent) || _stopping)
        {
            try { userAgent.AcceptCall(request).Reject(SIPResponseStatusCodesEnum.ServiceUnavailable, "Gateway unavailable"); } catch { }
            return;
        }

        if (userAgent.IsCallActive || userAgent.IsCalling || Interlocked.CompareExchange(ref _callInProgress, 1, 0) != 0)
        {
            try { userAgent.AcceptCall(request).Reject(SIPResponseStatusCodesEnum.BusyHere, "Radio gateway busy"); } catch { }
            _log($"SIP: входящий вызов от {caller} на {targetForLog} отклонён: шлюз занят (486 Busy Here).");
            return;
        }

        SIPServerUserAgent? serverUserAgent = null;
        VoIPMediaSession? mediaSession = null;
        try
        {
            serverUserAgent = userAgent.AcceptCall(request);
            if (!_config.AutoAnswerForRtpTest)
            {
                serverUserAgent.Reject(SIPResponseStatusCodesEnum.ServiceUnavailable,
                    "RTP test auto-answer disabled");
                _log($"SIP: входящий вызов от {caller} на {targetForLog} отклонён с 503: SIP.AutoAnswerForRtpTest=false.");
                return;
            }

            serverUserAgent.Progress(SIPResponseStatusCodesEnum.Ringing, "RTP media test", null, null, null);
            _log($"SIP: отправлен 180 Ringing для вызова от {caller}; создаю тестовую RTP-медиасессию.");

            mediaSession = new VoIPMediaSession();
            mediaSession.AcceptRtpFromAny = true;
            AlsaAudioBridge? audioBridge = null;
            if (_audioConfig.BridgeEnabled)
            {
                audioBridge = new AlsaAudioBridge(_audioConfig, _log);
                audioBridge.Attach(mediaSession);
                _log("SIP: включён двусторонний ALSA/RTP аудиомост. PTT/GPIO остаётся отключённым.");
            }
            else
            {
                // Explicitly select Silence so this diagnostic session never sends music or tones.
                mediaSession.AudioExtrasSource.SetSource(AudioSourcesEnum.Silence);
                _log("SIP: тестовый RTP-источник установлен в Silence; музыка и тестовые тоны отключены.");
            }

            bool answered = await userAgent.Answer(serverUserAgent, mediaSession).ConfigureAwait(false);
            if (!answered)
            {
                mediaSession.Close("RTP test answer failed");
                mediaSession.Dispose();
                mediaSession = null;
                if (audioBridge != null) await audioBridge.DisposeAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _callInProgress, 0);
                _log($"SIP: не удалось ответить на вызов от {caller}; SIPSorcery не смог согласовать тестовую медиасессию.");
                return;
            }

            _activeTestMedia = mediaSession;
            if (audioBridge != null)
            {
                _activeAudioBridge = audioBridge;
                await audioBridge.StartAsync().ConfigureAwait(false);
                _log($"SIP: вызов от {caller} принят; двусторонний ALSA/RTP аудиомост запущен. PTT не задействован.");
            }
            else
            {
                _log($"SIP: тестовый вызов от {caller} на {targetForLog} принят; SIP/SDP/RTP сессия с тишиной поднята. ALSA/PTT не задействованы.");
            }
        }
        catch (Exception ex)
        {
            if (mediaSession != null)
            {
                try { mediaSession.Close("Incoming test call failed"); } catch { }
                try { mediaSession.Dispose(); } catch { }
            }
            var failedBridge = Interlocked.Exchange(ref _activeAudioBridge, null);
            if (failedBridge != null) _ = failedBridge.DisposeAsync();
            if (serverUserAgent != null)
            {
                try { serverUserAgent.Reject(SIPResponseStatusCodesEnum.ServiceUnavailable, "RTP test setup failed"); } catch { }
            }
            Interlocked.Exchange(ref _callInProgress, 0);
            _log($"SIP: ошибка обработки INVITE от {caller}: {ex.Message}");
        }
        finally
        {
            if (_activeTestMedia == null)
                Interlocked.Exchange(ref _callInProgress, 0);
        }
    }

    private static string ReadUser(string? user, string fallback) => string.IsNullOrWhiteSpace(user) ? fallback : user;

    private void HandleCallHungup(string callId)
    {
        var bridge = Interlocked.Exchange(ref _activeAudioBridge, null);
        if (bridge != null) _ = bridge.DisposeAsync();
        var media = Interlocked.Exchange(ref _activeTestMedia, null);
        if (media != null)
        {
            try { media.Close("SIP call ended"); } catch { }
            try { media.Dispose(); } catch { }
        }
        Interlocked.Exchange(ref _callInProgress, 0);
        _log($"SIP: вызов завершён (Call-ID {callId}).");
    }

    private void QueueRetry()
    {
        if (Interlocked.Exchange(ref _retryQueued, 1) == 0)
            _retryRequests.Writer.TryWrite(1);
    }

    private async Task RetryWorkerAsync()
    {
        try
        {
            while (await _retryRequests.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_retryRequests.Reader.TryRead(out _)) { }
                if (_stopping || _registered) { Interlocked.Exchange(ref _retryQueued, 0); continue; }
                _retryAttempt++;
                int multiplier = _retryAttempt switch { 1 => 1, 2 => 2, 3 => 3, _ => 3 };
                int delay = Math.Clamp(_config.RegistrationRetrySeconds * multiplier, 1, 60);
                _log($"SIP: повторная регистрация через {delay} с (попытка {_retryAttempt}; базовый интервал {_config.RegistrationRetrySeconds} с).");
                await Task.Delay(TimeSpan.FromSeconds(delay), _stop.Token).ConfigureAwait(false);
                if (_stopping || _registered) { Interlocked.Exchange(ref _retryQueued, 0); continue; }
                try { _registrationAgent?.Stop(false); } catch (Exception ex) { _log($"SIP: предупреждение при остановке старого агента регистрации: {ex.Message}"); }
                _registrationAgent = null;
                Interlocked.Exchange(ref _retryQueued, 0);
                StartRegistrationAgent();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log($"SIP: ошибка фонового механизма повторной регистрации: {ex.Message}");
            if (!_stopping) QueueRetry();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _registered = false;
        _stop.Cancel();
        _retryRequests.Writer.TryComplete();

        try { _callUserAgent?.Hangup(); } catch { }
        var bridge = Interlocked.Exchange(ref _activeAudioBridge, null);
        if (bridge != null)
        {
            try { await bridge.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        var media = Interlocked.Exchange(ref _activeTestMedia, null);
        if (media != null)
        {
            try { media.Close("Service stopping"); } catch { }
            try { media.Dispose(); } catch { }
        }
        try { _callUserAgent?.Dispose(); } catch (Exception ex) { _log($"SIP: предупреждение при остановке SIPUserAgent: {ex.Message}"); }
        _callUserAgent = null;

        try { _registrationAgent?.Stop(); }
        catch (Exception ex) { _log($"SIP: предупреждение при снятии регистрации: {ex.Message}"); }
        _registrationAgent = null;

        if (_retryWorker != null)
        {
            try { await _retryWorker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        try { _transport?.Shutdown(); }
        catch (Exception ex) { _log($"SIP: ошибка остановки транспорта: {ex.Message}"); }
        _transport = null;
        _stop.Dispose();
        _log("SIP: служба регистрации остановлена.");
    }
}
