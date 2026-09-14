using Whisper.Shared;

namespace Whisper.Tests.Client;

/// <summary>Synthetic signals so audio behaviour can be asserted without a sound card.</summary>
internal static class AudioSignals
{
    public static short[] Sine(double frequencyHz = 440, double amplitude = 0.5, int samples = AudioFormat.FrameSamples, int startSample = 0)
    {
        var frame = new short[samples];

        for (var i = 0; i < samples; i++)
        {
            var t = (startSample + i) / (double)AudioFormat.SampleRate;
            frame[i] = (short)(Math.Sin(2 * Math.PI * frequencyHz * t) * amplitude * short.MaxValue);
        }

        return frame;
    }

    public static short[] Silence(int samples = AudioFormat.FrameSamples) => new short[samples];

    /// <summary>Very low level noise, as a real microphone produces in a quiet room.</summary>
    public static short[] RoomNoise(int seed = 1, int samples = AudioFormat.FrameSamples)
    {
        var random = new Random(seed);
        var frame = new short[samples];

        for (var i = 0; i < samples; i++)
        {
            frame[i] = (short)(random.NextDouble() * 2 - 1);
        }

        return frame;
    }

    /// <summary>
    /// Normalised correlation between two frames, which survives the amplitude and phase
    /// changes a lossy codec introduces while still catching garbled output.
    /// </summary>
    public static double Correlation(ReadOnlySpan<short> left, ReadOnlySpan<short> right)
    {
        var length = Math.Min(left.Length, right.Length);
        double dot = 0, leftEnergy = 0, rightEnergy = 0;

        for (var i = 0; i < length; i++)
        {
            double a = left[i];
            double b = right[i];
            dot += a * b;
            leftEnergy += a * a;
            rightEnergy += b * b;
        }

        return leftEnergy <= 0 || rightEnergy <= 0 ? 0 : dot / Math.Sqrt(leftEnergy * rightEnergy);
    }

    /// <summary>
    /// Best correlation across a range of offsets. Opus introduces a few milliseconds of
    /// algorithmic delay, so a sample-aligned comparison understates quality badly; what
    /// matters is that the waveform comes out intact somewhere within that delay.
    /// </summary>
    public static double BestCorrelation(ReadOnlySpan<short> reference, ReadOnlySpan<short> candidate, int maxLag)
    {
        var length = Math.Min(reference.Length, candidate.Length) - maxLag;
        var best = 0d;

        for (var lag = 0; lag <= maxLag; lag++)
        {
            best = Math.Max(best, Correlation(reference.Slice(lag, length), candidate[..length]));
        }

        return best;
    }

    public static double RmsDb(ReadOnlySpan<short> frame)
    {
        double sum = 0;

        foreach (var sample in frame)
        {
            var normalised = sample / (double)short.MaxValue;
            sum += normalised * normalised;
        }

        var rms = Math.Sqrt(sum / frame.Length);
        return rms <= 0 ? double.NegativeInfinity : 20 * Math.Log10(rms);
    }

    /// <summary>Overload for mixer output, which is already normalised to -1..1.</summary>
    public static double RmsDb(ReadOnlySpan<float> block)
    {
        double sum = 0;

        foreach (var sample in block)
        {
            sum += (double)sample * sample;
        }

        var rms = Math.Sqrt(sum / block.Length);
        return rms <= 0 ? double.NegativeInfinity : 20 * Math.Log10(rms);
    }
}
