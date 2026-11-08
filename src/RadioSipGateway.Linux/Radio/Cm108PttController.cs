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
        int releaseDelayMs,
        int totSeconds,
        int totLockoutSeconds,
        Action<string> log)
    {
        _path = path;
        _cosEnabled = cosEnabled;
        _cosActiveHigh = cosActiveHigh;
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
                // Fail-safe at startup: GPIO3 becomes an output and is driven LOW.
                WritePttLocked(false);
                _started = true;
            }
            catch
            {
                Native.close(_fd);
                _fd = -1;
                throw;
            }
        }

        _readTask = Task.Run(ReadLoop);
        _monitorTask = Task.Run(MonitorLoopAsync);
        _log($"CM108: {_path} открыт; GPIO3 настроен как выход LOW. PTT активен уровнем HIGH.");
        if (_cosEnabled)
            _log("COS: источник VOLDN; передача заблокирована до первого HID-отчёта с известным состоянием COS.");
        else
            _log("COS: контроль отключён; передача не будет блокироваться по занятости радиоканала.");
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
            _totLatched = false;
            _pttStartedAt = null;
            _blockedMessageShown = false;
            SetPttLocked(false, reason);
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
                    SetPttLocked(false, "потеря HID/COS-соединения");
                    _log("CM108: обработчик HID остановлен; дальнейшая передача запрещена до перезапуска приложения.");
                }
            }
        }
    }

    private void ProcessInputReport(byte[] data, int length)
    {
        if (!_cosEnabled || length < 1) return;
        bool voldnBitSet = (data[0] & VoldnMask) != 0;
        bool busy = _cosActiveHigh ? voldnBitSet : !voldnBitSet;
        lock (_gate)
        {
            if (_disposed) return;
            bool changed = !_cosKnown || _cosBusy != busy;
            _cosKnown = true;
            _cosBusy = busy;
            if (changed)
                _log(busy ? "COS: VOLDN активен — радиоканал ЗАНЯТ; PTT запрещён." : "COS: VOLDN неактивен — радиоканал СВОБОДЕН.");
            if (busy)
            {
                if (_pttOn) SetPttLocked(false, "COS стал активным");
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
                        _pttStartedAt = null;
                        SetPttLocked(false, "нет входящих SIP-аудиокадров");
                    }

                    if (_pttOn && _totSeconds > 0 && _pttStartedAt.HasValue && now - _pttStartedAt.Value >= TimeSpan.FromSeconds(_totSeconds))
                    {
                        SetPttLocked(false, "сработал SIP→радио TOT");
                        _totLatched = true;
                        _lockoutUntil = now.AddSeconds(_totLockoutSeconds);
                        _log($"PTT: превышен TOT ({_totSeconds} с); передача заблокирована до завершения текущей активности/вызова. Блокировка после TOT: {_totLockoutSeconds} с.");
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
        if (_cosEnabled && _cosBusy)
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
        if (!_pttOn)
        {
            SetPttLocked(true, "поступили аудиокадры SIP→радио");
            if (_pttOn) _pttStartedAt = now;
        }
    }

    private void SetPttLocked(bool high, string reason)
    {
        if (_pttOn == high) return;
        try
        {
            WritePttLocked(high);
            _pttOn = high;
            if (!high) _pttStartedAt = null;
            _log(high ? $"PTT: GPIO3 HIGH — ПЕРЕДАЧА ВКЛЮЧЕНА ({reason})." : $"PTT: GPIO3 LOW — передача выключена ({reason}).");
        }
        catch (Exception ex)
        {
            _faulted = true;
            _pttRequested = false;
            _pttOn = false;
            _log($"PTT: ОШИБКА HID-управления; дальнейшая передача запрещена: {ex.GetType().Name}: {ex.Message}");
            try
            {
                // A failed HIGH/LOW report must never lead to another TX attempt.
                // Make one independent best-effort attempt to return the line to LOW.
                WritePttLocked(false);
                _log("PTT: дополнительная попытка установить GPIO3 LOW выполнена.");
            }
            catch (Exception lowError)
            {
                _log($"PTT: критическая ошибка — не удалось повторно установить GPIO3 LOW: {lowError.Message}");
            }
        }
    }

    private void WritePttLocked(bool high)
    {
        if (_fd < 0) throw new IOException("CM108 HID уже закрыт.");
        // hidraw write: report ID 0 + HID_OR0 + HID_OR1 + HID_OR2 + HID_OR3.
        // GPIO3 output mode: OR2 bit 2 = 1; GPIO3 level: OR1 bit 2.
        byte[] report = { 0x00, 0x00, high ? Gpio3Mask : (byte)0x00, Gpio3Mask, 0x00 };
        nint written = Native.write(_fd, report, (nuint)report.Length);
        if (written != (nint)report.Length)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"Запись HID-отчёта PTT не выполнена полностью (written={written}, errno={error}).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        int fdToClose;
        lock (_gate)
        {
            if (_disposed) return;
            _pttRequested = false;
            if (_fd >= 0)
            {
                try
                {
                    WritePttLocked(false);
                    _pttOn = false;
                    _log("PTT: GPIO3 LOW при остановке CM108-контроллера.");
                }
                catch (Exception ex)
                {
                    _log($"PTT: не удалось отправить GPIO3 LOW при остановке: {ex.Message}");
                }
            }
            _disposed = true;
            fdToClose = _fd;
            _fd = -1;
        }
        _stop.Cancel();
        if (fdToClose >= 0) Native.close(fdToClose);
        foreach (Task? task in new[] { _readTask, _monitorTask })
        {
            if (task == null) continue;
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { _log("CM108: поток HID не завершился за 2 с после закрытия устройства."); }
            catch (OperationCanceledException) { }
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
