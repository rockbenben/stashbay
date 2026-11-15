namespace FileStash.App.Controls;

/// <summary>Monotonic countdown state; pausing preserves time for reading and interacting.</summary>
public sealed class ToastCountdown
{
    public ToastCountdown(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Duration = Remaining = duration;
    }

    public TimeSpan Duration { get; }
    public TimeSpan Remaining { get; private set; }
    public double Fraction => Remaining.TotalMilliseconds / Duration.TotalMilliseconds;
    public bool IsElapsed => Remaining == TimeSpan.Zero;

    public void Advance(TimeSpan elapsed, bool paused)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (!paused) Remaining = elapsed >= Remaining ? TimeSpan.Zero : Remaining - elapsed;
    }
}
