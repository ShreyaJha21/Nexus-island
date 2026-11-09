using System.Windows;
using System.Windows.Media.Animation;

namespace Nexus.Shell.Island;

/// <summary>
/// A critically damped spring: natural, with inertia, and no visible overshoot.
/// Used for Rise (about 250 ms) and Expand (about 300 ms), per the PRD motion spec.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    private const double K = 8;
    private static readonly double Norm = 1 - (1 + K) * Math.Exp(-K);

    public SpringEase()
    {
        EasingMode = EasingMode.EaseIn; // EaseIn = use EaseInCore as-is
    }

    /// <summary>Normalized time at which the spring has covered the given fraction of its distance.</summary>
    public static double TimeAt(double fraction)
    {
        double lo = 0, hi = 1;
        for (var i = 0; i < 30; i++)
        {
            var mid = (lo + hi) / 2;
            if (Value(mid) < fraction) lo = mid; else hi = mid;
        }

        return hi;
    }

    protected override double EaseInCore(double t) => Value(t);

    protected override Freezable CreateInstanceCore() => new SpringEase();

    private static double Value(double t) => (1 - (1 + K * t) * Math.Exp(-K * t)) / Norm;
}

/// <summary>The motion spec from the PRD, in one place.</summary>
public static class Motion
{
    public static readonly TimeSpan Rise = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan Sink = TimeSpan.FromMilliseconds(167);
    public static readonly TimeSpan Expand = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan Collapse = TimeSpan.FromMilliseconds(167);
    public static readonly TimeSpan ContentOut = TimeSpan.FromMilliseconds(83);
    public static readonly TimeSpan ContentIn = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan ReducedCrossfade = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(1000);

    /// <summary>Content fades in once the shape is 60% grown.</summary>
    public static readonly TimeSpan ContentInDelay = TimeSpan.FromMilliseconds(Expand.TotalMilliseconds * SpringEase.TimeAt(0.6));

    /// <summary>"Reduce motion": Windows' "Animation effects" switch.</summary>
    public static bool Reduced => !SystemParameters.ClientAreaAnimation || ForceReduced;

    private static readonly bool ForceReduced = Environment.GetEnvironmentVariable("NEXUS_REDUCED_MOTION") == "1";

    public static readonly IEasingFunction Spring = new SpringEase();
    public static readonly IEasingFunction Accelerate = new CubicEase { EasingMode = EasingMode.EaseIn };
    public static readonly IEasingFunction Standard = new CubicEase { EasingMode = EasingMode.EaseInOut };
    public static readonly IEasingFunction Decelerate = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static DoubleAnimation To(double to, TimeSpan duration, IEasingFunction? ease = null, TimeSpan? delay = null) => new(to, duration)
    {
        EasingFunction = ease,
        BeginTime = delay ?? TimeSpan.Zero,
        FillBehavior = FillBehavior.HoldEnd,
    };
}
