namespace RadioSipGateway.Linux.Configuration;

internal sealed class GatewayConfig
{
    public SipConfig Sip { get; } = new();
    public AudioConfig Audio { get; } = new();
    public TransmitConfig Transmit { get; } = new();
    public VoxConfig Vox { get; } = new();
    public RadioConfig Radio { get; } = new();
    public LoggingConfig Logging { get; } = new();
}

internal sealed class SipConfig
{
    public string Server { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5060;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int LocalPort { get; set; } = 5062;
    public int RegistrationRetrySeconds { get; set; } = 10;
}

internal sealed class AudioConfig
{
    public string CaptureDevice { get; set; } = "default";
    public string PlaybackDevice { get; set; } = "default";
    public int SampleRate { get; set; } = 48000;
    public bool KeepDeviceOpen { get; set; } = true;
}

internal sealed class TransmitConfig
{
    public string Mode { get; set; } = "cm108";
    public bool VoxEnabled { get; set; } = false;
    public string HidrawPath { get; set; } = "";
    public int PttReleaseDelayMs { get; set; } = 250;
}

internal sealed class VoxConfig
{
    public int ThresholdDbfs { get; set; } = -45;
    public int ActivationDelayMs { get; set; } = 120;
    public int ReleaseDelayMs { get; set; } = 700;
}

internal sealed class RadioConfig
{
    public int SipToRadioTotSeconds { get; set; } = 180;
    public int SipToRadioTotLockoutSeconds { get; set; } = 10;
    public int GlobalTotSeconds { get; set; } = 0;
    public bool CourtesyTonesEnabled { get; set; } = true;
    public int CourtesyToneToRadioLevelPercent { get; set; } = 45;
    public int CourtesyToneToSipLevelPercent { get; set; } = 25;
    public int TotWarningToneLevelPercent { get; set; } = 65;
    public bool IncomingRadioAlertEnabled { get; set; } = true;
    public int IncomingRadioAlertLevelPercent { get; set; } = 45;
    public bool RingbackToneToRadioEnabled { get; set; } = true;
    public int RingbackToneLevelPercent { get; set; } = 60;
    public bool ForwardRadioDtmfToSip { get; set; } = true;
    public bool AutoCallOnHash { get; set; } = true;
    public string ManualDialNumber { get; set; } = "";
    public string CallsignWavPath { get; set; } = "/etc/radiosipgateway/callsign.wav";
    public string IncomingWavPath { get; set; } = "";
    public bool IncomingWavEnabled { get; set; } = false;
}

internal sealed class LoggingConfig
{
    public string Level { get; set; } = "Information";
}
