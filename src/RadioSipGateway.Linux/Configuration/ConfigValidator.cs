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
        if (c.Transmit.PttEnabled && c.Transmit.Mode != "cm108") throw new InvalidOperationException("Transmit.PttEnabled=true поддерживается только при Transmit.Mode=cm108.");
        if (c.Transmit.PttEnabled && string.IsNullOrWhiteSpace(c.Transmit.HidrawPath)) throw new InvalidOperationException("Для Transmit.PttEnabled=true необходимо задать Transmit.HidrawPath, например /dev/hidraw0.");
        if (c.Transmit.PttActiveLevel is not ("high" or "low")) throw new InvalidOperationException("Transmit.PttActiveLevel должен быть High или Low.");
        if (c.Transmit.PttPin != "gpio3") throw new InvalidOperationException("На данном этапе поддерживается только Transmit.PttPin=gpio3.");
        if (c.Transmit.CosSource != "voldn") throw new InvalidOperationException("На данном этапе поддерживается только Transmit.CosSource=voldn.");
        if (c.Transmit.PttEnabled && (!c.Audio.BridgeEnabled || !c.Sip.AutoAnswerForRtpTest)) throw new InvalidOperationException("Для тестового PTT включите SIP.AutoAnswerForRtpTest=true и Audio.BridgeEnabled=true.");
        if (c.Transmit.PttEnabled && !c.Transmit.VoxEnabled)
            Console.WriteLine("ПРЕДУПРЕЖДЕНИЕ: VoxEnabled=false — любой входящий RTP-кадр, включая цифровую тишину, удерживает запрос PTT. Для передачи только во время речи включите Transmit.VoxEnabled=true.");
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
        if (c.Radio.SipToRadioTotLockoutSeconds < 0 || c.Radio.SipToRadioTotLockoutSeconds > 3600) throw new InvalidOperationException("Radio.SipToRadioTotLockoutSeconds должен быть в диапазоне 0..3600.");
        if (c.Radio.GlobalTotSeconds < 0 || c.Radio.GlobalTotSeconds > 86400) throw new InvalidOperationException("Radio.GlobalTotSeconds должен быть в диапазоне 0..86400.");
        if (c.Radio.SipRadioCompressorSpeedMs < 10 || c.Radio.SipRadioCompressorSpeedMs > 1000) throw new InvalidOperationException("Radio.SipRadioCompressorSpeedMs должен быть в диапазоне 10..1000.");
        if (c.Radio.SipPlaybackGainDb < -24 || c.Radio.SipPlaybackGainDb > 18) throw new InvalidOperationException("Radio.SipPlaybackGainDb должен быть в диапазоне -24..18 дБ.");
    }

    private static void Port(int p, string key) { if (p is < 1 or > 65535) throw new InvalidOperationException($"{key} должен быть в диапазоне 1..65535."); }
    private static void Percent(int p, string key) { if (p is < 0 or > 100) throw new InvalidOperationException($"{key} должен быть в диапазоне 0..100."); }
}
