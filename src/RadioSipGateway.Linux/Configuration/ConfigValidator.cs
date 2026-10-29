namespace RadioSipGateway.Linux.Configuration;

internal static class ConfigValidator
{
    public static void Validate(GatewayConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.Sip.Server)) throw new InvalidOperationException("SIP.Server не может быть пустым.");
        Port(c.Sip.Port, "SIP.Port");
        Port(c.Sip.LocalPort, "SIP.LocalPort");
        if (c.Sip.RegistrationRetrySeconds < 1 || c.Sip.RegistrationRetrySeconds > 3600) throw new InvalidOperationException("SIP.RegistrationRetrySeconds должен быть в диапазоне 1..3600.");
        if (c.Sip.RegistrationExpirySeconds < 30 || c.Sip.RegistrationExpirySeconds > 3600) throw new InvalidOperationException("SIP.RegistrationExpirySeconds должен быть в диапазоне 30..3600.");
        if (c.Audio.SampleRate is not (8000 or 16000 or 32000 or 44100 or 48000)) throw new InvalidOperationException("Audio.SampleRate: допустимы 8000, 16000, 32000, 44100 или 48000.");
        if (c.Transmit.Mode is not ("cm108" or "vox" or "none")) throw new InvalidOperationException("Transmit.Mode должен быть cm108, vox или none.");
        if (c.Transmit.PttReleaseDelayMs < 0 || c.Transmit.PttReleaseDelayMs > 5000) throw new InvalidOperationException("Transmit.PttReleaseDelayMs должен быть в диапазоне 0..5000.");
        if (c.Vox.ThresholdDbfs < -120 || c.Vox.ThresholdDbfs > -1) throw new InvalidOperationException("VOX.ThresholdDbfs должен быть в диапазоне -120..-1 dBFS.");
        if (c.Vox.ActivationDelayMs < 0 || c.Vox.ActivationDelayMs > 5000) throw new InvalidOperationException("VOX.ActivationDelayMs должен быть в диапазоне 0..5000.");
        if (c.Vox.ReleaseDelayMs < 50 || c.Vox.ReleaseDelayMs > 10000) throw new InvalidOperationException("VOX.ReleaseDelayMs должен быть в диапазоне 50..10000.");
        if (c.Transmit.Mode == "cm108" && string.IsNullOrWhiteSpace(c.Transmit.HidrawPath))
            Console.WriteLine("ПРЕДУПРЕЖДЕНИЕ: Transmit.HidrawPath не задан; путь к CM108 HID будет определён на этапе аппаратной настройки.");
        Percent(c.Radio.CourtesyToneToRadioLevelPercent, "Radio.CourtesyToneToRadioLevelPercent");
        Percent(c.Radio.CourtesyToneToSipLevelPercent, "Radio.CourtesyToneToSipLevelPercent");
        Percent(c.Radio.TotWarningToneLevelPercent, "Radio.TotWarningToneLevelPercent");
        Percent(c.Radio.IncomingRadioAlertLevelPercent, "Radio.IncomingRadioAlertLevelPercent");
        Percent(c.Radio.RingbackToneLevelPercent, "Radio.RingbackToneLevelPercent");
        if (c.Radio.SipToRadioTotSeconds < 0 || c.Radio.SipToRadioTotSeconds > 3600) throw new InvalidOperationException("Radio.SipToRadioTotSeconds должен быть в диапазоне 0..3600.");
        if (c.Radio.GlobalTotSeconds < 0 || c.Radio.GlobalTotSeconds > 86400) throw new InvalidOperationException("Radio.GlobalTotSeconds должен быть в диапазоне 0..86400.");
    }

    private static void Port(int p, string key) { if (p is < 1 or > 65535) throw new InvalidOperationException($"{key} должен быть в диапазоне 1..65535."); }
    private static void Percent(int p, string key) { if (p is < 0 or > 100) throw new InvalidOperationException($"{key} должен быть в диапазоне 0..100."); }
}
