namespace Puck.Commands;

/// <summary>
/// Classifies how a <see cref="FeatureSwitchDescriptor"/>'s value reaches the engine — the axis a benchmark harness
/// (or the <c>feature.*</c> verbs) reasons about when it decides whether flipping a switch is cheap or costs a rebuild.
/// </summary>
/// <remarks>
/// The kind is metadata, not enforcement: every switch is applied through the same <c>Set</c> delegate. It exists so a
/// caller can present the switch honestly (a per-frame flag versus a program rebuild) and so a sweep can budget its
/// settle discipline accordingly.
/// </remarks>
public enum FeatureSwitchKind {
    /// <summary>The value flows through a per-frame channel and takes effect on the next produced frame — no rebuild,
    /// near-zero cost to flip (for example a shader branch packed into the per-frame params).</summary>
    FrameFlag = 0,

    /// <summary>Applying the value rebuilds an engine program or pipeline, so a caller must let the engine settle before
    /// it measures (the extra warm frames a sweep already re-runs absorb this).</summary>
    RebuildRequired,

    /// <summary>The value is one of a small fixed set of named tiers (the switch's <see cref="FeatureSwitchDescriptor.AllowedValues"/>),
    /// rather than an on/off flag — for example a render-scale or present-rate tier.</summary>
    EnumTier,
}
