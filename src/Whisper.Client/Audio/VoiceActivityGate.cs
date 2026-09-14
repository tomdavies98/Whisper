namespace Whisper.Client.Audio;

/// <summary>
/// Decides whether a captured frame should be transmitted. Pure and frame-at-a-time: the
/// only state is the level history and how long the gate has been held open.
/// </summary>
public sealed class VoiceActivityGate(TimeProvider timeProvider)
{
    private long? _lastOpenTicks;

    /// <summary>Level below which a frame counts as silence, in dBFS.</summary>
    public float ThresholdDb { get; set; } = -45f;

    /// <summary>
    /// How long the gate stays open after the level drops. Without this, quiet consonants
    /// at the end of a word get chopped off.
    /// </summary>
    public TimeSpan Hangover { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Most recent frame level in dBFS, for the input meter.</summary>
    public float LastLevelDb { get; private set; } = float.NegativeInfinity;

    /// <summary>Most recent frame level as 0..1, for a progress-bar style meter.</summary>
    public float LastLevel { get; private set; }

    public bool IsOpen { get; private set; }

    public bool Process(ReadOnlySpan<short> frame)
    {
        var rmsDb = MeasureDb(frame);
        var peakDb = MeasurePeakDb(frame);
        // Use the louder of RMS and peak so the dBFS threshold matches what the input
        // meter shows. A peak-only spike still counts; steady speech is covered by RMS.
        LastLevelDb = float.IsNegativeInfinity(rmsDb) ? peakDb
            : float.IsNegativeInfinity(peakDb) ? rmsDb
            : Math.Max(rmsDb, peakDb);
        LastLevel = ToDisplayLevel(LastLevelDb);

        var now = timeProvider.GetUtcNow().UtcTicks;

        if (LastLevelDb >= ThresholdDb)
        {
            _lastOpenTicks = now;
            IsOpen = true;
            return true;
        }

        IsOpen = _lastOpenTicks is { } lastOpen && now - lastOpen <= Hangover.Ticks;
        return IsOpen;
    }

    public void Reset()
    {
        _lastOpenTicks = null;
        IsOpen = false;
        LastLevel = 0f;
        LastLevelDb = float.NegativeInfinity;
    }

    public static float MeasureDb(ReadOnlySpan<short> frame)
    {
        if (frame.Length == 0)
        {
            return float.NegativeInfinity;
        }

        double sumOfSquares = 0;

        foreach (var sample in frame)
        {
            var normalised = sample / (double)short.MaxValue;
            sumOfSquares += normalised * normalised;
        }

        var rms = Math.Sqrt(sumOfSquares / frame.Length);
        return rms <= 0 ? float.NegativeInfinity : (float)(20 * Math.Log10(rms));
    }

    public static float MeasurePeakDb(ReadOnlySpan<short> frame)
    {
        var peak = 0;
        foreach (var sample in frame)
        {
            var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak <= 0 ? float.NegativeInfinity : (float)(20 * Math.Log10(peak / (double)short.MaxValue));
    }

    /// <summary>
    /// Maps -70..0 dBFS onto 0..1. A floor of -60 left quiet headset mics looking dead.
    /// </summary>
    public static float ToDisplayLevel(float levelDb) =>
        float.IsNegativeInfinity(levelDb) ? 0f : Math.Clamp((levelDb + 70f) / 70f, 0f, 1f);
}
