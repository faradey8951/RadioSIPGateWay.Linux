using System;

namespace RadioSipGateway.Linux.Radio
{
    /// <summary>
    /// Streaming DTMF decoder for 16-bit mono PCM. Uses Goertzel energy measurements at
    /// the eight nominal DTMF frequencies. Stricter spectral dominance, row/column balance,
    /// and multi-frame qualification reduce speech false positives.
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

        public event Action<char>? DigitDetected;
        public event Action<bool>? ToneActivityChanged;

        public DtmfDecoder(int sampleRate)
        {
            if (sampleRate < 8000) throw new ArgumentOutOfRangeException("sampleRate");
            _sampleRate = sampleRate;
            _frame = new short[Math.Max(1, sampleRate * FrameMilliseconds / 1000)];
        }

        /// <summary>Feed little-endian, signed 16-bit mono PCM bytes from the ALSA capture pipeline.</summary>
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

            // Publish tone activity separately from the one-shot digit event. The radio RTP
            // source uses this to keep acoustic DTMF out of the speech stream while forwarding
            // the decoded digit as an RFC4733/RTP telephone-event.
            if (current.HasValue)
            {
                _nonToneFrames = 0;
                if (!_toneActive)
                {
                    if (_toneActivityCandidate == current.Value)
                        _toneActivityCandidateFrames++;
                    else
                    {
                        _toneActivityCandidate = current.Value;
                        _toneActivityCandidateFrames = 1;
                    }

                    if (_toneActivityCandidateFrames >= ToneActivityConfirmFrames)
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

            if (!current.HasValue)
            {
                _candidate = '\0';
                _candidateFrames = 0;
                // Only quiet frames release a latched key. A noisy or distorted DTMF frame
                // must not be mistaken for the gap between digits.
                if (lowLevel)
                {
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
                }
                return null;
            }

            _silenceFrames = 0;
            if (_candidate == current.Value)
            {
                _candidateFrames++;
            }
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
                return current.Value;
            }

            return null;
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
            if (wasToneActive)
            {
                Action<bool>? activityHandler = ToneActivityChanged;
                if (activityHandler != null) activityHandler(false);
            }
        }
    }
}
