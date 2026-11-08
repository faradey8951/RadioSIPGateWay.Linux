namespace RadioSipGateway.Linux.Radio;

/// <summary>
/// Speech leveler for decoded SIP audio sent to the radio. Mirrors the Windows
/// Stage 42 normalizer: target RMS -26 dBFS, up to -18/+12 dB correction and
/// a soft peak limiter. The response time is based on the actual PCM rate.
/// </summary>
internal sealed class SipRadioCompressor
{
    private const double TargetRmsDbfs = -26.0;
    private const double MinimumCorrectionDb = -18.0;
    private const double MaximumCorrectionDb = 12.0;
    private const double LimiterKnee = 0.82;
    private const double LimiterRange = 0.08;

    private double _correctionDb;
    private bool _hasGainTarget;
    private int _speedMilliseconds = 120;

    public int SpeedMilliseconds
    {
        get => _speedMilliseconds;
        set => _speedMilliseconds = Math.Clamp(value, 10, 1000);
    }

    public void Process(short[] samples, bool speechActive, double playbackGainDb, bool enabled, int sampleRate)
    {
        if (samples == null || samples.Length == 0) return;
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        double inputGain = Math.Pow(10.0, playbackGainDb / 20.0);
        if (!enabled)
        {
            Reset();
            ApplyLinearGainAndClip(samples, inputGain);
            return;
        }

        if (speechActive)
        {
            double sumSquares = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                double value = samples[i] / 32768.0;
                sumSquares += value * value;
            }

            double rms = Math.Sqrt(sumSquares / samples.Length);
            if (rms > 0.0005)
            {
                double inputDbfs = 20.0 * Math.Log10(rms);
                double wantedCorrectionDb = Math.Clamp(TargetRmsDbfs - inputDbfs,
                    MinimumCorrectionDb, MaximumCorrectionDb);

                if (!_hasGainTarget)
                {
                    _correctionDb = wantedCorrectionDb;
                    _hasGainTarget = true;
                }
                else
                {
                    double blockMilliseconds = samples.Length * 1000.0 / sampleRate;
                    double coefficient = 1.0 - Math.Exp(-blockMilliseconds / _speedMilliseconds);
                    _correctionDb += (wantedCorrectionDb - _correctionDb) * coefficient;
                }
            }
        }

        ApplySoftLimitedGain(samples, inputGain * Math.Pow(10.0, _correctionDb / 20.0));
    }

    public void Reset()
    {
        _correctionDb = 0.0;
        _hasGainTarget = false;
    }

    private static void ApplyLinearGainAndClip(short[] samples, double gain)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double value = Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
            samples[i] = (short)Math.Round(value);
        }
    }

    private static void ApplySoftLimitedGain(short[] samples, double gain)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double value = samples[i] / 32768.0 * gain;
            double magnitude = Math.Abs(value);
            if (magnitude > LimiterKnee)
            {
                double over = magnitude - LimiterKnee;
                magnitude = LimiterKnee + LimiterRange * (1.0 - Math.Exp(-over / LimiterRange));
                value = Math.Sign(value) * magnitude;
            }
            value = Math.Clamp(value, -0.999, 0.999);
            samples[i] = (short)Math.Round(value * 32767.0);
        }
    }
}
