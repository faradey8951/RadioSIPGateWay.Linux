using System;

namespace RadioSipGateway.Linux.Radio
{
    /// <summary>
    /// Streaming radio-control tone decoder for 16-bit mono PCM. Uses Goertzel energy
    /// measurements for the eight DTMF frequencies and the 1750 Hz repeater-access tone.
    /// Spectral dominance and multi-frame qualification reduce speech false positives.
    /// </summary>
    internal sealed class DtmfDecoder
    {
        private const int FrameMilliseconds = 20;
        private const double MinimumRms = 180.0; // approx. -45 dBFS; retain sensitivity for radio DTMF.
        // Each of the two DTMF tones must contribute a meaningful share of total frame energy.
        // The previous 0.035 threshold admitted speech formants that happened to resemble a pair.
        private const double MinimumNormalizedTonePower = 0.075;
        // Best row/column must clearly dominate the other three candidates (power ratio).
        private const double MinimumDominanceRatio = 6.3; // approx. 8 dB.
        private const int RequiredToneFrames = 3; // 60 ms of stable pair before accepting a digit.
        private const int RequiredSilenceFramesToRelease = 2; // 40 ms before re-arming the same digit.
        private const int ToneActivityConfirmFrames = 2; // avoid muting ordinary speech for one spurious frame.

        // Quansheng and other handhelds may send a 1750 Hz repeater-access burst instead of DTMF.
        // The decoder accepts a narrow band around 1750 Hz after three consecutive 20 ms frames.
        private const double RepeaterToneMinimumRms = 220.0;
        private const double RepeaterToneMinimumNormalizedPower = 0.24;
        private const double RepeaterToneMinimumDominanceRatio = 4.0;
        private const int RepeaterToneRequiredFrames = 3; // 60 ms qualification.
        private const int RepeaterToneReleaseFrames = 4; // 80 ms before accepting another burst.
        private static readonly double[] RepeaterToneFrequencies = { 1725.0, 1750.0, 1775.0 };
        private static readonly double[] RepeaterToneGuardFrequencies = { 1625.0, 1875.0 };

        private static readonly double[] RowFrequencies = { 697.0, 770.0, 852.0, 941.0 };
        private static readonly double[] ColumnFrequencies = { 1209.0, 1336.0, 1477.0, 1633.0 };
        private static readonly char[,] KeyMap =
        {
            { '1', '2', '3', 'A' },
            { '4', '5', '6', 'B' },
            { '7', '8', '9', 'C' },
            { '*', '0', '#', 'D' }
        };

        private readonly int _sampleRate;
        private readonly short[] _frame;
        private int _frameCount;
        private char _candidate;
        private int _candidateFrames;
        private int _silenceFrames;
        private bool _digitLatched;
        private char _latchedDigit;
        private bool _toneActive;
        private int _nonToneFrames;
        private char _toneActivityCandidate;
        private int _toneActivityCandidateFrames;
        private const int ToneReleaseFrames = 2;
        private bool _repeaterToneLatched;
        private int _repeaterToneCandidateFrames;
        private int _repeaterToneReleaseFrameCount;

        public event Action<char>? DigitDetected;
        public event Action<bool>? ToneActivityChanged;
        public event Action? RepeaterTone1750Detected;

        public DtmfDecoder(int sampleRate)
        {
            if (sampleRate < 8000) throw new ArgumentOutOfRangeException("sampleRate");
            _sampleRate = sampleRate;
            _frame = new short[Math.Max(1, sampleRate * FrameMilliseconds / 1000)];
        }

        /// <summary>Feed little-endian, signed 16-bit mono PCM bytes from NAudio.</summary>
        public void ProcessBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException("offset");

            int end = offset + count;
            // Audio format is mono PCM16, so each sample occupies two bytes.
            for (int i = offset; i + 1 < end; i += 2)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                _frame[_frameCount++] = sample;
                if (_frameCount == _frame.Length)
                {
                    char? detected = ProcessFrame();
                    _frameCount = 0;
                    if (detected.HasValue)
                    {
                        Action<char>? handler = DigitDetected;
                        if (handler != null) handler(detected.Value);
                    }
                }
            }
        }

        private char? ProcessFrame()
        {
            double mean = 0.0;
            for (int i = 0; i < _frame.Length; i++) mean += _frame[i];
            mean /= _frame.Length;

            double sumSquares = 0.0;
            for (int i = 0; i < _frame.Length; i++)
            {
                double sample = _frame[i] - mean;
                sumSquares += sample * sample;
            }

            double rms = Math.Sqrt(sumSquares / _frame.Length);
            char? current = null;
            bool lowLevel = rms < MinimumRms;
            if (!lowLevel && sumSquares > 0.0)
                current = DetectPair(sumSquares, mean);

            // Only check 1750 Hz when the frame is not already a valid DTMF pair.
            // This prevents a DTMF digit from being reported a second time as a repeater burst.
            bool repeaterTone = !current.HasValue && rms >= RepeaterToneMinimumRms &&
                sumSquares > 0.0 && DetectRepeaterTone1750(sumSquares, mean);

            // Keep both DTMF and the 1750 Hz access burst out of the outgoing speech stream.
            // For the narrowband 1750 Hz burst one valid frame is enough to mute that 20 ms
            // block; DTMF retains its existing two-frame confirmation.
            bool anyTone = current.HasValue || repeaterTone;
            if (anyTone)
            {
                _nonToneFrames = 0;
                if (!_toneActive)
                {
                    char activityKey = current.HasValue ? current.Value : '\u0001';
                    if (_toneActivityCandidate == activityKey)
                        _toneActivityCandidateFrames++;
                    else
                    {
                        _toneActivityCandidate = activityKey;
                        _toneActivityCandidateFrames = 1;
                    }

                    int requiredActivityFrames = repeaterTone ? 1 : ToneActivityConfirmFrames;
                    if (_toneActivityCandidateFrames >= requiredActivityFrames)
                    {
                        _toneActive = true;
                        Action<bool>? activityHandler = ToneActivityChanged;
                        if (activityHandler != null) activityHandler(true);
                    }
                }
            }
            else
            {
                _toneActivityCandidate = '\0';
                _toneActivityCandidateFrames = 0;
                if (_toneActive)
                {
                    _nonToneFrames++;
                    if (_nonToneFrames >= ToneReleaseFrames)
                    {
                        _toneActive = false;
                        _nonToneFrames = 0;
                        Action<bool>? activityHandler = ToneActivityChanged;
                        if (activityHandler != null) activityHandler(false);
                    }
                }
            }

            // Existing DTMF digit latch and inter-digit release logic.
            char? dtmfDetected = null;
            if (!current.HasValue)
            {
                _candidate = '\0';
                _candidateFrames = 0;
                _silenceFrames++;
                if (_silenceFrames >= RequiredSilenceFramesToRelease)
                {
                    _digitLatched = false;
                    _latchedDigit = '\0';
                }
            }
            else
            {
                _silenceFrames = 0;
                if (_candidate == current.Value)
                    _candidateFrames++;
                else
                {
                    _candidate = current.Value;
                    _candidateFrames = 1;
                }

                // A stable change to a different key may be emitted without waiting for a full
                // quiet frame; repeated presses of the same key still require a quiet gap.
                if (_candidateFrames >= RequiredToneFrames && (!_digitLatched || current.Value != _latchedDigit))
                {
                    _digitLatched = true;
                    _latchedDigit = current.Value;
                    dtmfDetected = current.Value;
                }
            }

            // A held 1750 Hz key produces one '*' event. It must be released for 80 ms
            // before another press can be accepted, so a long transmission cannot repeat.
            if (repeaterTone)
            {
                _repeaterToneReleaseFrameCount = 0;
                if (!_repeaterToneLatched)
                {
                    _repeaterToneCandidateFrames++;
                    if (_repeaterToneCandidateFrames >= RepeaterToneRequiredFrames)
                    {
                        _repeaterToneLatched = true;
                        _repeaterToneCandidateFrames = 0;
                        Action? handler = RepeaterTone1750Detected;
                        if (handler != null) handler();
                    }
                }
            }
            else
            {
                _repeaterToneCandidateFrames = 0;
                if (_repeaterToneLatched)
                {
                    _repeaterToneReleaseFrameCount++;
                    if (_repeaterToneReleaseFrameCount >= RepeaterToneReleaseFrames)
                    {
                        _repeaterToneLatched = false;
                        _repeaterToneReleaseFrameCount = 0;
                    }
                }
                else
                {
                    _repeaterToneReleaseFrameCount = 0;
                }
            }

            return dtmfDetected;
        }

        private bool DetectRepeaterTone1750(double sumSquares, double mean)
        {
            double bestTonePower = 0.0;
            for (int i = 0; i < RepeaterToneFrequencies.Length; i++)
            {
                double power = CalculatePower(RepeaterToneFrequencies[i], mean);
                if (power > bestTonePower) bestTonePower = power;
            }

            // A true access tone is a narrow, dominant spectral line. Guard bins on both sides
            // reject speech formants, broadband hiss and the upper DTMF group (including 1633 Hz).
            double strongestGuardPower = 0.0;
            for (int i = 0; i < RepeaterToneGuardFrequencies.Length; i++)
            {
                double power = CalculatePower(RepeaterToneGuardFrequencies[i], mean);
                if (power > strongestGuardPower) strongestGuardPower = power;
            }

            double normalizedPower = bestTonePower / (_frame.Length * sumSquares);
            if (normalizedPower < RepeaterToneMinimumNormalizedPower)
                return false;

            if (strongestGuardPower > 0.0 && bestTonePower / strongestGuardPower < RepeaterToneMinimumDominanceRatio)
                return false;

            return true;
        }

        private double CalculatePower(double frequency, double mean)
        {
            double omega = 2.0 * Math.PI * frequency / _sampleRate;
            double coefficient = 2.0 * Math.Cos(omega);
            double q1 = 0.0;
            double q2 = 0.0;

            for (int i = 0; i < _frame.Length; i++)
            {
                double sample = _frame[i] - mean;
                double q0 = sample + coefficient * q1 - q2;
                q2 = q1;
                q1 = q0;
            }

            double power = q1 * q1 + q2 * q2 - coefficient * q1 * q2;
            return power > 0.0 ? power : 0.0;
        }

        private char? DetectPair(double sumSquares, double mean)
        {
            // Use the same DC-corrected frame for Goertzel measurements and RMS validation.
            double[] rowPowers = CalculatePowers(RowFrequencies, mean);
            double[] columnPowers = CalculatePowers(ColumnFrequencies, mean);

            int rowIndex;
            int columnIndex;
            double bestRow = FindMaximum(rowPowers, out rowIndex);
            double bestColumn = FindMaximum(columnPowers, out columnIndex);
            double secondRow = FindSecondMaximum(rowPowers, rowIndex);
            double secondColumn = FindSecondMaximum(columnPowers, columnIndex);

            // Relative energy prevents ordinary speech/noise from being accepted solely because
            // its strongest frequency happens to be close to one DTMF frequency.
            double rowRatio = bestRow / (_frame.Length * sumSquares);
            double columnRatio = bestColumn / (_frame.Length * sumSquares);
            if (rowRatio < MinimumNormalizedTonePower || columnRatio < MinimumNormalizedTonePower)
                return null;

            if (secondRow <= 0.0 || secondColumn <= 0.0) return null;
            if (bestRow / secondRow < MinimumDominanceRatio || bestColumn / secondColumn < MinimumDominanceRatio)
                return null;

            // Reject excessive low/high-group twist. Speech can generate two apparent peaks,
            // but their energies are often badly unbalanced compared with a real keypad tone.
            double twist = bestRow / bestColumn;
            if (twist < 0.16 || twist > 6.25) return null;

            return KeyMap[rowIndex, columnIndex];
        }

        private double[] CalculatePowers(double[] frequencies, double mean)
        {
            var powers = new double[frequencies.Length];
            for (int i = 0; i < frequencies.Length; i++)
            {
                double omega = 2.0 * Math.PI * frequencies[i] / _sampleRate;
                double coefficient = 2.0 * Math.Cos(omega);
                double q1 = 0.0;
                double q2 = 0.0;

                for (int j = 0; j < _frame.Length; j++)
                {
                    double sample = _frame[j] - mean;
                    double q0 = sample + coefficient * q1 - q2;
                    q2 = q1;
                    q1 = q0;
                }

                powers[i] = q1 * q1 + q2 * q2 - coefficient * q1 * q2;
                if (powers[i] < 0.0) powers[i] = 0.0;
            }
            return powers;
        }

        private static double FindMaximum(double[] values, out int index)
        {
            index = 0;
            double maximum = values[0];
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] > maximum)
                {
                    maximum = values[i];
                    index = i;
                }
            }
            return maximum;
        }

        private static double FindSecondMaximum(double[] values, int maximumIndex)
        {
            double second = 0.0;
            for (int i = 0; i < values.Length; i++)
                if (i != maximumIndex && values[i] > second) second = values[i];
            return second;
        }

        public void Reset()
        {
            bool wasToneActive = _toneActive;
            _frameCount = 0;
            _candidate = '\0';
            _candidateFrames = 0;
            _silenceFrames = 0;
            _digitLatched = false;
            _latchedDigit = '\0';
            _toneActive = false;
            _nonToneFrames = 0;
            _toneActivityCandidate = '\0';
            _toneActivityCandidateFrames = 0;
            _repeaterToneLatched = false;
            _repeaterToneCandidateFrames = 0;
            _repeaterToneReleaseFrameCount = 0;
            if (wasToneActive)
            {
                Action<bool>? activityHandler = ToneActivityChanged;
                if (activityHandler != null) activityHandler(false);
            }
        }
    }
}
