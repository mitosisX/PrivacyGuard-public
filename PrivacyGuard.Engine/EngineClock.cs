using System.Diagnostics;

namespace PrivacyGuard.Engine;

/// <summary>
/// Milliseconds on the performance-counter clock: the same clock Windows uses to time-stamp
/// captured frames, so box positions can be predicted for the exact moment they are shown.
/// </summary>
public static class EngineClock
{
    public static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}
