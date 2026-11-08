using System;

namespace RadioSipGateway.Linux.Radio
{
    /// <summary>
    /// Cascaded second-order Butterworth high-pass and low-pass filters.
    /// The passband is bounded by the configured cutoff frequencies and filter
    /// state is preserved between buffers to avoid clicks at frame boundaries.
    /// If the upper cutoff is at or above Nyquist for the current sample rate,
    /// the low-pass stage is bypassed (the signal cannot contain frequencies above Nyquist).
    /// </summary>
    internal sealed class AudioBandPassFilter
    {
        private readonly double _lowCutoffHz;
        private readonly double _highCutoffHz;
        private int _sampleRate;
        private Biquad _highPass;
        private Biquad _lowPass;
        private bool _lowPassEnabled;

        public AudioBandPassFilter(double lowCutoffHz, double highCutoffHz)
        {
            if (lowCutoffHz <= 0.0) throw new ArgumentOutOfRangeException(nameof(lowCutoffHz));
            if (highCutoffHz <= lowCutoffHz) throw new ArgumentOutOfRangeException(nameof(highCutoffHz));
            _lowCutoffHz = lowCutoffHz;
            _highCutoffHz = highCutoffHz;
        }

        /// <summary>Returns a filtered copy of signed 16-bit mono PCM samples.</summary>
        public short[]? Process(short[]? input, int sampleRate)
        {
            if (input == null) return null;
            if (input.Length == 0) return new short[0];
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            EnsureConfigured(sampleRate);
            short[] output = new short[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                double value = ProcessConfigured(input[i]);
                if (value > short.MaxValue) value = short.MaxValue;
                else if (value < short.MinValue) value = short.MinValue;
                output[i] = (short)Math.Round(value);
            }
            return output;
        }

        /// <summary>
        /// Filters one normalized floating-point sample. Intended for a single
        /// continuous audio callback; instances must not be used concurrently.
        /// </summary>
        public float ProcessSample(float input, int sampleRate)
        {
            EnsureConfigured(sampleRate);
            double value = ProcessConfigured(input);
            if (value > 1.0) value = 1.0;
            else if (value < -1.0) value = -1.0;
            return (float)value;
        }

        public void Reset()
        {
            _sampleRate = 0;
            _highPass = default(Biquad);
            _lowPass = default(Biquad);
            _lowPassEnabled = false;
        }

        private void EnsureConfigured(int sampleRate)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (_sampleRate == sampleRate) return;

            _highPass = Biquad.CreateHighPass(sampleRate, _lowCutoffHz);
            _lowPassEnabled = _highCutoffHz < sampleRate * 0.5;
            _lowPass = _lowPassEnabled
                ? Biquad.CreateLowPass(sampleRate, _highCutoffHz)
                : default(Biquad);
            _sampleRate = sampleRate;
        }

        private double ProcessConfigured(double input)
        {
            double value = _highPass.Process(input);
            if (_lowPassEnabled)
                value = _lowPass.Process(value);
            return value;
        }

        /// <summary>Direct Form I biquad with a second-order Butterworth Q.</summary>
        private struct Biquad
        {
            private double _b0;
            private double _b1;
            private double _b2;
            private double _a1;
            private double _a2;
            private double _x1;
            private double _x2;
            private double _y1;
            private double _y2;

            public static Biquad CreateHighPass(int sampleRate, double cutoffHz)
            {
                return Create(sampleRate, cutoffHz, true);
            }

            public static Biquad CreateLowPass(int sampleRate, double cutoffHz)
            {
                return Create(sampleRate, cutoffHz, false);
            }

            private static Biquad Create(int sampleRate, double cutoffHz, bool highPass)
            {
                const double q = 0.7071067811865476;
                double omega = 2.0 * Math.PI * cutoffHz / sampleRate;
                double cosine = Math.Cos(omega);
                double sine = Math.Sin(omega);
                double alpha = sine / (2.0 * q);
                double a0 = 1.0 + alpha;
                double b0;
                double b1;
                double b2;

                if (highPass)
                {
                    b0 = (1.0 + cosine) * 0.5 / a0;
                    b1 = -(1.0 + cosine) / a0;
                    b2 = (1.0 + cosine) * 0.5 / a0;
                }
                else
                {
                    b0 = (1.0 - cosine) * 0.5 / a0;
                    b1 = (1.0 - cosine) / a0;
                    b2 = (1.0 - cosine) * 0.5 / a0;
                }

                return new Biquad
                {
                    _b0 = b0,
                    _b1 = b1,
                    _b2 = b2,
                    _a1 = (-2.0 * cosine) / a0,
                    _a2 = (1.0 - alpha) / a0
                };
            }

            public double Process(double input)
            {
                double output = _b0 * input + _b1 * _x1 + _b2 * _x2
                    - _a1 * _y1 - _a2 * _y2;

                _x2 = _x1;
                _x1 = input;
                _y2 = _y1;
                _y1 = output;
                return output;
            }
        }
    }
}
