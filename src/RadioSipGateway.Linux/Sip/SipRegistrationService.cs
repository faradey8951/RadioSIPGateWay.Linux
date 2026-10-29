using System.Net;
using System.Threading.Channels;
using RadioSipGateway.Linux.Configuration;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace RadioSipGateway.Linux.Sip;

/// <summary>
/// SIP REGISTER lifecycle for the Linux port. This first milestone intentionally
/// handles registration only; call signaling and media are added in later stages.
/// </summary>
internal sealed class SipRegistrationService : IAsyncDisposable
{
    private readonly SipConfig _config;
    private readonly Action<string> _log;
    private readonly Channel<byte> _retryRequests = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _registrationAgent;
    private Task? _retryWorker;
    private int _retryAttempt;
    private int _retryQueued;
    private volatile bool _registered;
    private volatile bool _stopping;

    public SipRegistrationService(SipConfig config, Action<string> log)
    {
        _config = config;
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
            _retryWorker = Task.Run(RetryWorkerAsync);
            StartRegistrationAgent();
            _log($"SIP: UDP transport слушает порт {_config.LocalPort}; регистрация на {_config.Server}:{_config.Port} запущена.");
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
        var agent = new SIPRegistrationUserAgent(_transport, _config.Username, _config.Password, registrar, 120);
        _registrationAgent = agent;

        agent.RegistrationSuccessful += (uri, response) =>
        {
            if (!ReferenceEquals(_registrationAgent, agent) || _stopping) return;
            _registered = true;
            _retryAttempt = 0;
            Interlocked.Exchange(ref _retryQueued, 0);
            _log($"SIP: регистрация успешна: {uri}.");
        };

        agent.RegistrationFailed += (uri, response, error) =>
        {
            if (!ReferenceEquals(_registrationAgent, agent) || _stopping) return;
            _registered = false;
            string detail = response == null
                ? error
                : $"{response.StatusCode} {response.ReasonPhrase}" + (string.IsNullOrWhiteSpace(error) ? "" : $" — {error}");
            _log($"SIP: ошибка регистрации: {detail}");
            QueueRetry();
        };

        agent.RegistrationTemporaryFailure += (uri, response, message) =>
        {
            if (!ReferenceEquals(_registrationAgent, agent) || _stopping) return;
            _log($"SIP: временная ошибка регистрации: {message}");
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
                _log($"SIP: повторная регистрация через {delay} с (попытка {_retryAttempt}).");
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
