namespace RadioSipGateway.Linux.Radio;

/// <summary>Deterministic smoke test for the ported DTMF decoder, without audio hardware.</summary>
internal static class DtmfDecoderSelfTest
{
    private const int SampleRate = 8000;
    private static readonly Dictionary<char, (double Low, double High)> Frequencies = new()
    {
        ['1'] = (697, 1209), ['2'] = (697, 1336), ['3'] = (697, 1477), ['A'] = (697, 1633),
        ['4'] = (770, 1209), ['5'] = (770, 1336), ['6'] = (770, 1477), ['B'] = (770, 1633),
        ['7'] = (852, 1209), ['8'] = (852, 1336), ['9'] = (852, 1477), ['C'] = (852, 1633),
        ['*'] = (941, 1209), ['0'] = (941, 1336), ['#'] = (941, 1477), ['D'] = (941, 1633)
    };

    public static int Run(Action<string> log)
    {
        var decoder = new DtmfDecoder(SampleRate);
        var detected = new List<char>();
        decoder.DigitDetected += detected.Add;

        foreach (char digit in new[] { '1', '*', '#', '0', '9' })
        {
            FeedInIrregularChunks(decoder, GenerateTone(digit, 180));
            FeedInIrregularChunks(decoder, GenerateSilence(100));
        }

        char[] expected = { '1', '*', '#', '0', '9' };
        if (!detected.SequenceEqual(expected))
        {
            log($"DTMF self-test FAILED. Expected [{string.Join(",", expected)}], got [{string.Join(',', detected)}].");
            return 1;
        }

        log($"DTMF self-test OK: decoded {string.Join(" ", detected)} from synthetic PCM at {SampleRate} Hz.");
        return 0;
    }

    private static byte[] GenerateTone(char digit, int milliseconds)
    {
        var (low, high) = Frequencies[digit];
        int count = SampleRate * milliseconds / 1000;
        var pcm = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / SampleRate;
            // Equal components, with enough headroom to avoid clipping.
            short sample = (short)(5000 * Math.Sin(2 * Math.PI * low * t) + 5000 * Math.Sin(2 * Math.PI * high * t));
            pcm[2 * i] = (byte)(sample & 0xff);
            pcm[2 * i + 1] = (byte)((sample >> 8) & 0xff);
        }
        return pcm;
    }

    private static byte[] GenerateSilence(int milliseconds) => new byte[SampleRate * milliseconds / 1000 * 2];

    private static void FeedInIrregularChunks(DtmfDecoder decoder, byte[] pcm)
    {
        // Deliberately split at non-frame boundaries to verify streaming behavior.
        int[] chunkSizes = { 222, 510, 94, 704, 318, 1024 };
        int offset = 0;
        int chunk = 0;
        while (offset < pcm.Length)
        {
            int count = Math.Min(chunkSizes[chunk++ % chunkSizes.Length], pcm.Length - offset);
            // PCM16 must be passed as whole samples.
            count &= ~1;
            if (count == 0) break;
            decoder.ProcessBuffer(pcm, offset, count);
            offset += count;
        }
    }
}
