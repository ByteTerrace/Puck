using System.Globalization;

namespace Puck.SdfVm;

/// <summary>The part of a lighting-converged frame's own work the irradiance volume still waits for
/// (<see cref="SdfMeshIrradiance.Convergence"/>), in the order the frame takes them.</summary>
public enum SdfMeshConvergenceStage : byte {
    /// <summary>No converging frame waits on the volume.</summary>
    None = 0,
    /// <summary>The trace scene of the frame's own static draws is building.</summary>
    Scene = 1,
    /// <summary>The windows the frame moved are being refilled from the probe cache.</summary>
    Refill = 2,
    /// <summary>The background trace is finishing its batch before the drain takes over its work.</summary>
    Yield = 3,
    /// <summary>The drain traces and relights the frame's windows to the end.</summary>
    Drain = 4,
}
/// <summary>Where a lighting-converged frame's own work stands, which its held frame names as its waiting reason.</summary>
/// <param name="Stage">The part still waited for.</param>
/// <param name="Pass">The drain's pass, from zero: the first traces and relights from no bounce, each later one relights
/// from the bounce the one before left. Zero before the drain.</param>
/// <param name="Done">The probes the drain's current pass has landed.</param>
/// <param name="Total">The probes each of the drain's passes takes: every probe of the windows. Zero before the
/// drain.</param>
public readonly record struct SdfMeshConvergence(SdfMeshConvergenceStage Stage, int Pass, int Done, int Total) {
    /// <summary>Returns the stage as the held frame names it, such as <c>drain pass 2, 4096 of 49152 probes</c>.</summary>
    /// <returns>The stage's text; empty for <see cref="SdfMeshConvergenceStage.None"/>.</returns>
    public override string ToString() => Stage switch {
        SdfMeshConvergenceStage.Scene => "its trace scene building",
        SdfMeshConvergenceStage.Refill => "its windows refilling",
        SdfMeshConvergenceStage.Yield => "the background trace yielding",
        SdfMeshConvergenceStage.Drain => string.Create(provider: CultureInfo.InvariantCulture, handler: $"drain pass {(Pass + 1)}, {Done} of {Total} probes"),
        _ => string.Empty,
    };
}
