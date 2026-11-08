using System.Runtime.InteropServices;

namespace RadioSipGateway.Linux.Radio;

/// <summary>
/// CM108 GPIO3 PTT plus VOLDN/COS input for Linux hidraw.
/// Verified wire format on CM108 0d8c:000c:
/// output report bytes: report-id, HID_OR0, HID_OR1, HID_OR2, HID_OR3.
/// GPIO3 is bit 2 (0x04) in HID_OR1; bit 2 in HID_OR2 configures it as output.
/// Input report byte 0 bit 1 (0x02) is VOLDN.
/// </summary>
internal sealed class Cm108PttController : IAsyncDisposable
{
    private const int O_RDWR = 0x0002;
    private const int O_NONBLOCK = 0x0800;
    private const int O_CLOEXEC = 0x80000;
    private const short POLLIN = 0x0001;
    private const short POLLERR = 0x0008;
    private const short POLLHUP = 0x0010;
    private const short POLLNVAL = 0x0020;
    private const int Eintr = 4;
    private const int Eagain = 11;
    private const byte Gpio3Mask = 0x04;
    private const byte VoldnMask = 0x02;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _cosEnabled;
    private readonly bool _cosActiveHigh;
    private readonly bool _pttActiveHigh;
    private readonly int _releaseDelayMs;
    private readonly int _totSeconds;
    private readonly int _totLockoutSeconds;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private int _fd = -1;
    private Task? _readTask;
    private Task? _monitorTask;
    private bool _started;
    private bool _disposed;
    private bool _faulted;
    private bool _cosKnown;
    private bool _cosBusy;
    // Logical PTT request/state, not the physical GPIO voltage.
    private bool _pttRequested;
    private bool _pttOn;
    private bool _totLatched;
    private bool _blockedMessageShown;
    private DateTimeOffset _lastAudioAt;
    private DateTimeOffset? _pttStartedAt;
    private DateTimeOffset _lockoutUntil = DateTimeOffset.MinValue;

    public Cm108PttController(
        string path,
        bool cosEnabled,
        bool cosActiveHigh,
        bool pttActiveHigh,
        int releaseDelayMs,
        int totSeconds,
        int totLockoutSeconds,
        Action<string> log)
    {
        _path = path;
        _cosEnabled = cosEnabled;
        _cosActiveHigh = cosActiveHigh;
        _pttActiveHigh = pttActiveHigh;
        _releaseDelayMs = releaseDelayMs;
        _totSeconds = totSeconds;
        _totLockoutSeconds = totLockoutSeconds;
        _log = log;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            int fd = Native.open(_path, O_RDWR | O_NONBLOCK | O_CLOEXEC);
            if (fd < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new IOException($"Не удалось открыть CM108 HID {_path}: errno={error} ({new System.ComponentModel.Win32Exception(error).Message}). Проверьте путь и права группы audio.");
            }
            _fd = fd;
            try
            {
                // Always drive the configured inactive level before starting any reader/timer.
                // Repeat the report to avoid relying on a single USB HID write at startup.
                for (int i = 0; i < 3; i++)
                {
                    WritePttOutputLevelLocked(outputHigh: !_pttActiveHigh);
                    if (i < 2) Thread.Sleep(20);
                }
                _pttOn = false;
                _started = true;
            }
            catch
            {
                // The first safe write may have failed partially. Make one more best-effort
                // attempt before closing the HID handle.
                TryWriteIdleLocked("ошибка инициализации CM108");
                Native.close(_fd);
                _fd = -1;
                throw;
            }
        }

        _readTask = Task.Run(ReadLoop);
        _monitorTask = Task.Run(MonitorLoopAsync);
        string active = _pttActiveHigh ? "HIGH" : "LOW";
        string idle = _pttActiveHigh ? "LOW" : "HIGH";
        _log($"CM108: {_path} открыт; GPIO3 настроен как выход. Активный уровень PTT={active}; безопасный уровень={idle}.");
        if (_cosEnabled)
            _log($"COS: источник VOLDN; уровень {( _cosActiveHigh ? "HIGH" : "LOW" )} означает ЗАНЯТО. До первого HID-отчёта передача заблокирована.");
        else
            _log("COS: контроль отключён; передача не блокируется по занятости радиоканала.");
    }

    /// <summary>Called for every decoded remote SIP audio frame considered active.</summary>
    public void NotifyRemoteAudio()
    {
        lock (_gate)
        {
            if (!_started || _disposed || _faulted) return;
            _pttRequested = true;
            _lastAudioAt = DateTimeOffset.UtcNow;
            UpdatePttLocked();
        }
    }

    /// <summary>Release request at the end of a SIP call or media failure.</summary>
    public void ReleaseTransmit(string reason)
    {
        lock (_gate)
        {
            if (!_started || _disposed) return;
            _pttRequested = false;
            _pttStartedAt = null;
            _totLatched = false;
            _blockedMessageShown = false;
            SetPttLocked(false, reason);
        }
    }

    /// <summary>
    /// Synchronous best-effort release used directly from Ctrl+C/SIGTERM handlers,
    /// before asynchronous SIP/ALSA teardown starts.
    /// </summary>
    public void EmergencyRelease(string reason)
    {
        lock (_gate)
        {
            if (_fd < 0 || _disposed) return;
            _pttRequested = false;
            _pttStartedAt = null;
            _totLatched = false;
            _blockedMessageShown = false;
            TryWriteIdleLocked(reason);
            _pttOn = false;
        }
    }

    private void ReadLoop()
    {
        byte[] buffer = new byte[64];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int fd;
                lock (_gate) fd = _fd;
                if (fd < 0) break;

                var pollFd = new PollFd { Fd = fd, Events = POLLIN };
                int ready = Native.poll(ref pollFd, (nuint)1, 250);
                if (_stop.IsCancellationRequested) break;
                if (ready < 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == Eintr) continue;
                    _log($"CM108: ошибка ожидания HID-отчёта: errno={error}.");
                    break;
                }
                if (ready == 0) continue;
                if ((pollFd.Revents & (POLLERR | POLLHUP | POLLNVAL)) != 0)
                {
                    _log($"CM108: HID-устройство сообщило ошибку/отключение (revents=0x{pollFd.Revents:X}).");
                    break;
                }
                if ((pollFd.Revents & POLLIN) == 0) continue;

                nint count = Native.read(fd, buffer, (nuint)buffer.Length);
                if (count < 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == Eagain || error == Eintr) continue;
                    if (!_stop.IsCancellationRequested) _log($"CM108: ошибка чтения HID: errno={error}.");
                    break;
                }
                if (count == 0) continue;
                ProcessInputReport(buffer, (int)count);
            }
        }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
                _log($"CM108: ошибка обработчика COS: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!_stop.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _faulted = true;
                    _pttRequested = false;
                    _pttStartedAt = null;
                    TryWriteIdleLocked("потеря HID/COS-соединения");
                    _pttOn = false;
                    _log("CM108: обработчик HID остановлен; дальнейшая передача запрещена до перезапуска приложения.");
                }
            }
        }
    }

    private void ProcessInputReport(byte[] data, int length)
    {
        if (!_cosEnabled || length < 1) return;
        byte raw = data[0];
        bool voldnBitSet = (raw & VoldnMask) != 0;
        bool busy = _cosActiveHigh ? voldnBitSet : !voldnBitSet;
        lock (_gate)
        {
            if (_disposed) return;
            bool changed = !_cosKnown || _cosBusy != busy;
            _cosKnown = true;
            _cosBusy = busy;
            if (changed)
                _log($"COS: VOLDN raw=0x{raw:X2}, активная полярность={(_cosActiveHigh ? "HIGH" : "LOW")} — {(busy ? "радиоканал ЗАНЯТ; PTT запрещён" : "радиоканал СВОБОДЕН")}.");
            if (busy)
            {
                // COS is an interlock for starting a transmission. Do not drop an
                // already-keyed half-duplex transmitter when its own RF causes COS
                // to assert; after PTT is released, the busy state blocks re-keying.
                if (_pttOn)
                    _log("COS: канал стал занятым во время собственной передачи; текущий PTT сохраняется до окончания аудио, повторное включение после отпускания будет заблокировано.");
                _blockedMessageShown = false;
            }
            else
            {
                _blockedMessageShown = false;
                UpdatePttLocked();
            }
        }
    }

    private async Task MonitorLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(50, _stop.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || !_started) break;
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    if (_pttRequested && _releaseDelayMs >= 0 && now - _lastAudioAt >= TimeSpan.FromMilliseconds(_releaseDelayMs))
                    {
                        _pttRequested = false;
                        SetPttLocked(false, "нет входящих SIP-аудиокадров");
                    }

                    if (_pttOn && _totSeconds > 0 && _pttStartedAt.HasValue && now - _pttStartedAt.Value >= TimeSpan.FromSeconds(_totSeconds))
                    {
                        SetPttLocked(false, "сработал SIP→радио TOT");
                        _totLatched = true;
                        _lockoutUntil = now.AddSeconds(_totLockoutSeconds);
                        _log($"PTT: превышен TOT ({_totSeconds} с); передача заблокирована до окончания текущего вызова. Блокировка после TOT: {_totLockoutSeconds} с.");
                    }
                    else
                    {
                        UpdatePttLocked();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void UpdatePttLocked()
    {
        if (_disposed || _faulted || !_started) return;
        if (!_pttRequested || _totLatched)
        {
            SetPttLocked(false, "нет запроса передачи");
            return;
        }
        if (_cosEnabled && !_cosKnown)
        {
            if (!_blockedMessageShown)
            {
                _log("PTT: ожидание первого COS/VOLDN-отчёта; передача пока запрещена.");
                _blockedMessageShown = true;
            }
            return;
        }
        if (_cosEnabled && _cosBusy && !_pttOn)
        {
            if (!_blockedMessageShown)
            {
                _log("PTT: передача заблокирована — радиоканал занят по COS.");
                _blockedMessageShown = true;
            }
            SetPttLocked(false, "радиоканал занят");
            return;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < _lockoutUntil)
        {
            if (!_blockedMessageShown)
            {
                _log($"PTT: передача заблокирована после TOT ещё {(int)Math.Ceiling((_lockoutUntil - now).TotalSeconds)} с.");
                _blockedMessageShown = true;
            }
            return;
        }
        _blockedMessageShown = false;
        if (!_pttOn) SetPttLocked(true, "поступили аудиокадры SIP→радио");
    }

    private void SetPttLocked(bool transmit, string reason)
    {
        if (_pttOn == transmit) return;
        try
        {
            WritePttForTransmitLocked(transmit);
            _pttOn = transmit;
            _pttStartedAt = transmit ? DateTimeOffset.UtcNow : null;
            bool outputHigh = transmit ? _pttActiveHigh : !_pttActiveHigh;
            string level = outputHigh ? "HIGH" : "LOW";
            _log(transmit
                ? $"PTT: GPIO3 {level} — ПЕРЕДАЧА ВКЛЮЧЕНА ({reason})."
                : $"PTT: GPIO3 {level} — передача выключена ({reason}).");
        }
        catch (Exception ex)
        {
            _faulted = true;
            _pttRequested = false;
            _pttOn = false;
            _pttStartedAt = null;
            _log($"PTT: ОШИБКА HID-управления; дальнейшая передача запрещена: {ex.GetType().Name}: {ex.Message}");
            TryWriteIdleLocked("аварийная попытка после ошибки PTT");
        }
    }

    private void WritePttForTransmitLocked(bool transmit)
    {
        bool outputHigh = transmit ? _pttActiveHigh : !_pttActiveHigh;
        WritePttOutputLevelLocked(outputHigh);
    }

    private void WritePttOutputLevelLocked(bool outputHigh)
    {
        if (_fd < 0) throw new IOException("CM108 HID уже закрыт.");
        // hidraw write: report ID 0 + HID_OR0 + HID_OR1 + HID_OR2 + HID_OR3.
        // GPIO3 output mode: OR2 bit 2 = 1; GPIO3 level: OR1 bit 2.
        byte[] report = { 0x00, 0x00, outputHigh ? Gpio3Mask : (byte)0x00, Gpio3Mask, 0x00 };
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            nint written = Native.write(_fd, report, (nuint)report.Length);
            if (written == (nint)report.Length) return;
            int error = Marshal.GetLastPInvokeError();
            if (written < 0 && error == Eintr) continue;
            if (written < 0 && error == Eagain && attempt < 3)
            {
                Thread.Sleep(10);
                continue;
            }
            throw new IOException($"Запись HID-отчёта GPIO3 не выполнена полностью (written={written}, errno={error}).");
        }
    }

    private void TryWriteIdleLocked(string reason)
    {
        if (_fd < 0) return;
        bool outputHigh = !_pttActiveHigh;
        int succeeded = 0;
        Exception? lastError = null;
        // Re-send the inactive state several times. This is deliberately done on startup,
        // Ctrl+C/SIGTERM and disposal so a single lost USB/HID report cannot leave TX keyed.
        for (int i = 0; i < 3; i++)
        {
            try
            {
                WritePttOutputLevelLocked(outputHigh);
                succeeded++;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            if (i < 2) Thread.Sleep(20);
        }
        if (succeeded > 0)
        {
            _pttOn = false;
            string idle = outputHigh ? "HIGH" : "LOW";
            _log($"PTT: аварийный уровень GPIO3 {idle} ({reason}); успешно отправлено отчётов: {succeeded}/3.");
        }
        else if (lastError != null)
        {
            _log($"PTT: КРИТИЧЕСКАЯ ОШИБКА — не удалось установить безопасный уровень GPIO3 ({reason}): {lastError.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _stop.Cancel();
        lock (_gate)
        {
            if (_disposed) return;
            _pttRequested = false;
            _pttStartedAt = null;
            _totLatched = false;
            TryWriteIdleLocked("остановка CM108-контроллера");
            _pttOn = false;
            _disposed = true;
        }

        // Let poll/read and monitor loops observe cancellation before closing their fd.
        foreach (Task? task in new[] { _readTask, _monitorTask })
        {
            if (task == null) continue;
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { _log("CM108: поток HID не завершился за 2 с при остановке."); }
            catch (OperationCanceledException) { }
        }

        lock (_gate)
        {
            // Final repeated idle report immediately before closing hidraw.
            TryWriteIdleLocked("последний сброс перед закрытием hidraw");
            int fd = _fd;
            _fd = -1;
            if (fd >= 0) Native.close(fd);
        }
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private static class Native
    {
        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int open(string pathname, int flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern nint read(int fd, [Out] byte[] buffer, nuint count);

        [DllImport("libc", SetLastError = true)]
        internal static extern nint write(int fd, [In] byte[] buffer, nuint count);

        [DllImport("libc", SetLastError = true)]
        internal static extern int poll(ref PollFd fds, nuint nfds, int timeout);

        [DllImport("libc", SetLastError = true)]
        internal static extern int close(int fd);
    }
}
