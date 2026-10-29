using System.Globalization;

namespace RadioSipGateway.Linux.Configuration;

internal static class IniConfigLoader
{
    public static GatewayConfig Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл конфигурации не найден", path);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        int lineNumber = 0;

        foreach (string raw in File.ReadLines(path))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Length == 0) throw new FormatException($"Пустая секция в строке {lineNumber}.");
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0) throw new FormatException($"Ожидался параметр key=value в строке {lineNumber}.");
            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            string fullKey = $"{section}.{key}";
            if (!values.TryAdd(fullKey, value)) throw new FormatException($"Повтор параметра {fullKey} в строке {lineNumber}.");
        }

        var config = new GatewayConfig();
        config.Sip.Server = Get(values, "SIP.Server", config.Sip.Server);
        config.Sip.Port = GetInt(values, "SIP.Port", config.Sip.Port);
        config.Sip.Username = Get(values, "SIP.Username", config.Sip.Username);
        config.Sip.Password = Get(values, "SIP.Password", config.Sip.Password);
        config.Sip.LocalPort = GetInt(values, "SIP.LocalPort", config.Sip.LocalPort);
        config.Sip.RegistrationRetrySeconds = GetInt(values, "SIP.RegistrationRetrySeconds", config.Sip.RegistrationRetrySeconds);
        config.Sip.RegistrationExpirySeconds = GetInt(values, "SIP.RegistrationExpirySeconds", config.Sip.RegistrationExpirySeconds);
        config.Sip.AutoAnswerForRtpTest = GetBool(values, "SIP.AutoAnswerForRtpTest", config.Sip.AutoAnswerForRtpTest);

        config.Audio.CaptureDevice = Get(values, "Audio.CaptureDevice", config.Audio.CaptureDevice);
        config.Audio.PlaybackDevice = Get(values, "Audio.PlaybackDevice", config.Audio.PlaybackDevice);
        config.Audio.SampleRate = GetInt(values, "Audio.SampleRate", config.Audio.SampleRate);
        config.Audio.KeepDeviceOpen = GetBool(values, "Audio.KeepDeviceOpen", config.Audio.KeepDeviceOpen);
        config.Audio.BridgeEnabled = GetBool(values, "Audio.BridgeEnabled", config.Audio.BridgeEnabled);

        config.Transmit.Mode = Get(values, "Transmit.Mode", config.Transmit.Mode).ToLowerInvariant();
        config.Transmit.VoxEnabled = GetBool(values, "Transmit.VoxEnabled", config.Transmit.VoxEnabled);
        config.Transmit.HidrawPath = Get(values, "Transmit.HidrawPath", config.Transmit.HidrawPath);
        config.Transmit.PttReleaseDelayMs = GetInt(values, "Transmit.PttReleaseDelayMs", config.Transmit.PttReleaseDelayMs);
        config.Vox.ThresholdDbfs = GetInt(values, "VOX.ThresholdDbfs", config.Vox.ThresholdDbfs);
        config.Vox.ActivationDelayMs = GetInt(values, "VOX.ActivationDelayMs", config.Vox.ActivationDelayMs);
        config.Vox.ReleaseDelayMs = GetInt(values, "VOX.ReleaseDelayMs", config.Vox.ReleaseDelayMs);

        config.Radio.SipToRadioTotSeconds = GetInt(values, "Radio.SipToRadioTotSeconds", config.Radio.SipToRadioTotSeconds);
        config.Radio.SipToRadioTotLockoutSeconds = GetInt(values, "Radio.SipToRadioTotLockoutSeconds", config.Radio.SipToRadioTotLockoutSeconds);
        config.Radio.GlobalTotSeconds = GetInt(values, "Radio.GlobalTotSeconds", config.Radio.GlobalTotSeconds);
        config.Radio.CourtesyTonesEnabled = GetBool(values, "Radio.CourtesyTonesEnabled", config.Radio.CourtesyTonesEnabled);
        config.Radio.CourtesyToneToRadioLevelPercent = GetInt(values, "Radio.CourtesyToneToRadioLevelPercent", config.Radio.CourtesyToneToRadioLevelPercent);
        config.Radio.CourtesyToneToSipLevelPercent = GetInt(values, "Radio.CourtesyToneToSipLevelPercent", config.Radio.CourtesyToneToSipLevelPercent);
        config.Radio.TotWarningToneLevelPercent = GetInt(values, "Radio.TotWarningToneLevelPercent", config.Radio.TotWarningToneLevelPercent);
        config.Radio.IncomingRadioAlertEnabled = GetBool(values, "Radio.IncomingRadioAlertEnabled", config.Radio.IncomingRadioAlertEnabled);
        config.Radio.IncomingRadioAlertLevelPercent = GetInt(values, "Radio.IncomingRadioAlertLevelPercent", config.Radio.IncomingRadioAlertLevelPercent);
        config.Radio.RingbackToneToRadioEnabled = GetBool(values, "Radio.RingbackToneToRadioEnabled", config.Radio.RingbackToneToRadioEnabled);
        config.Radio.RingbackToneLevelPercent = GetInt(values, "Radio.RingbackToneLevelPercent", config.Radio.RingbackToneLevelPercent);
        config.Radio.ForwardRadioDtmfToSip = GetBool(values, "Radio.ForwardRadioDtmfToSip", config.Radio.ForwardRadioDtmfToSip);
        config.Radio.AutoCallOnHash = GetBool(values, "Radio.AutoCallOnHash", config.Radio.AutoCallOnHash);
        config.Radio.ManualDialNumber = Get(values, "Radio.ManualDialNumber", config.Radio.ManualDialNumber);
        config.Radio.CallsignWavPath = Get(values, "Radio.CallsignWavPath", config.Radio.CallsignWavPath);
        config.Radio.IncomingWavPath = Get(values, "Radio.IncomingWavPath", config.Radio.IncomingWavPath);
        config.Radio.IncomingWavEnabled = GetBool(values, "Radio.IncomingWavEnabled", config.Radio.IncomingWavEnabled);
        config.Logging.Level = Get(values, "Logging.Level", config.Logging.Level);
        return config;
    }

    private static string Get(Dictionary<string, string> v, string k, string d) => v.TryGetValue(k, out var x) ? x : d;
    private static int GetInt(Dictionary<string, string> v, string k, int d) => v.TryGetValue(k, out var x) && int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : v.ContainsKey(k) ? throw new FormatException($"Параметр {k} должен быть целым числом.") : d;
    private static bool GetBool(Dictionary<string, string> v, string k, bool d) => v.TryGetValue(k, out var x) && bool.TryParse(x, out var b) ? b : v.ContainsKey(k) ? throw new FormatException($"Параметр {k} должен быть true или false.") : d;
}
