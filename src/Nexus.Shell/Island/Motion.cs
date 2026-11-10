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

/// <summary>
/// A lightly underdamped spring, after the Dynamic Island's lively morph: the shape overshoots its
/// target by about 4–5% and settles back within the duration. Used for growing (expand, new content).
/// </summary>
public sealed class BouncySpringEase : EasingFunctionBase
{
    private const double Zeta = 0.7;                       // damping ratio: ~4.6% overshoot
    private const double Omega = 6.0 / Zeta;               // settles (e^-6) by t = 1
    private static readonly double OmegaD = Omega * Math.Sqrt(1 - Zeta * Zeta);
    private static readonly double SinFactor = Zeta / Math.Sqrt(1 - Zeta * Zeta);

    public BouncySpringEase()
    {
        EasingMode = EasingMode.EaseIn; // EaseIn = use EaseInCore as-is
    }

    /// <summary>First normalized time at which the spring reaches the given fraction of its distance.</summary>
    public static double TimeAt(double fraction)
    {
        double lo = 0, hi = Math.PI / OmegaD; // monotonic up to the first peak
        for (var i = 0; i < 30; i++)
        {
            var mid = (lo + hi) / 2;
            if (Value(mid) < fraction) lo = mid; else hi = mid;
        }

        return hi;
    }

    protected override double EaseInCore(double t) => t >= 1 ? 1 : Value(t);

    protected override Freezable CreateInstanceCore() => new BouncySpringEase();

    private static double Value(double t) =>
        1 - Math.Exp(-Zeta * Omega * t) * (Math.Cos(OmegaD * t) + SinFactor * Math.Sin(OmegaD * t));
}

/// <summary>The motion spec from the PRD, in one place.</summary>
public static class Motion
{
    public static readonly TimeSpan Rise = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan Sink = TimeSpan.FromMilliseconds(167);
    /// <summary>A touch longer than Fluent's 300 ms so the springy settle has room to read.</summary>
    public static readonly TimeSpan Expand = TimeSpan.FromMilliseconds(380);
    public static readonly TimeSpan Collapse = TimeSpan.FromMilliseconds(167);
    public static readonly TimeSpan ContentOut = TimeSpan.FromMilliseconds(83);
    public static readonly TimeSpan ContentIn = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan ReducedCrossfade = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(1000);

    /// <summary>Content fades in once the shape is 60% grown.</summary>
    public static readonly TimeSpan ContentInDelay = TimeSpan.FromMilliseconds(Expand.TotalMilliseconds * BouncySpringEase.TimeAt(0.6));

    /// <summary>"Reduce motion": Windows' "Animation effects" switch.</summary>
    public static bool Reduced => !SystemParameters.ClientAreaAnimation || ForceReduced;

    private static readonly bool ForceReduced = Environment.GetEnvironmentVariable("NEXUS_REDUCED_MOTION") == "1";

    public static readonly IEasingFunction Spring = new SpringEase();

    /// <summary>Growing shapes use the livelier spring; rising out of the taskbar keeps the calm one.</summary>
    public static readonly IEasingFunction Bouncy = new BouncySpringEase();
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
