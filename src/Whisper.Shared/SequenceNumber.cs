namespace Whisper.Shared;

/// <summary>
/// 16-bit sequence arithmetic. Sequence numbers wrap roughly every 22 minutes of
/// continuous speech at 20 ms frames, so comparison has to be modular rather than
/// a plain integer compare or the jitter buffer would stall at the wrap point.
/// </summary>
public static class SequenceNumber
{
    public static ushort Next(ushort current) => unchecked((ushort)(current + 1));

    /// <summary>True when <paramref name="candidate"/> is ahead of <paramref name="reference"/>.</summary>
    public static bool IsNewer(ushort candidate, ushort reference) => Distance(candidate, reference) > 0;

    /// <summary>
    /// Signed distance from <paramref name="reference"/> to <paramref name="candidate"/>,
    /// positive when the candidate is newer. Half the range is treated as forwards.
    /// </summary>
    public static int Distance(ushort candidate, ushort reference) =>
        unchecked((short)(candidate - reference));
}
