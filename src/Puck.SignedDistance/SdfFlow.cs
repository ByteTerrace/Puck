using System.Numerics;
using System.Text.Json.Serialization;

using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.SignedDistance;

/// <summary>The frame a material's flow vector (<see cref="SdfFlow.Velocity"/>) is authored in.</summary>
[JsonConverter(typeof(StrictEnumConverter<SdfFlowFrame>))]
public enum SdfFlowFrame {
    /// <summary>The frame of the surface's own object: the creation's frame for a placed creation (its copies turn the
    /// vector with them), the slot's frame for a surface riding a slot, and world space for a surface emitted in world
    /// space (a field row).</summary>
    Object = 0,
    /// <summary>World space whatever the surface rides, the pattern included, so it runs continuously across every
    /// surface wearing the material: a wind across a meadow of separately swaying blades.</summary>
    World = 1,
}

/// <summary>A material's flow layer: the shading moves along an authored flow while the geometry stays still. The
/// layer's lookup point advects along <see cref="Velocity"/>, projected onto the surface, by a phase the host integrates
/// from the presented tick, in two layers half a <see cref="Cycle"/> apart whose weights cross-fade so neither restart
/// shows, with a per-place phase offset from a low-frequency noise so the surface never pulses as one (Vlachos's flow
/// shading, with a procedural pattern for the texture). The pattern is the seeded value-noise lattice the weathering
/// reads, sampled with its gradient; it writes a normal tilt of <see cref="Tilt"/> times its slope and a coverage of
/// <see cref="Coverage"/> that reveals <see cref="Surface"/>. Presentation only: the irradiance trace and every query
/// ignore it, and no kernel reads a clock (<c>SdfWorldPackage.FlowClock</c> carries the phase).</summary>
/// <param name="Velocity">The flow, in the frame's units per second, in <paramref name="Frame"/>; its tangential part
/// at a point is the speed the pattern moves there, so a flow running into a surface moves it less.</param>
/// <param name="Scale">The pattern's frequency, in lattice cells per unit; finite and positive.</param>
/// <param name="Stretch">How many times longer a pattern feature is along the flow than across it; finite, at least
/// one (one is isotropic).</param>
/// <param name="Cycle">The seconds one layer advects before it restarts, in [<see cref="MinCycle"/>,
/// <see cref="ClockPeriodSeconds"/>]. Rounded so a whole number of cycles fills the clock's period
/// (<see cref="CyclesPerClock"/>). A long cycle stretches the pattern further before it fades out.</param>
/// <param name="Tilt">The normal tilt, the slope the pattern's unit gradient (in lattice cells) gives the shading
/// normal; finite and non-negative, zero tilts nothing. Scaled by how much of the flow runs along the surface.</param>
/// <param name="Coverage">The share of the surface the pattern's coverage reveals <paramref name="Surface"/> over, in
/// [0, 1]; zero reveals nothing.</param>
/// <param name="Softness">The width of the coverage's edge in pattern units, in [0, 1].</param>
/// <param name="Surface">The surface the coverage reveals (streaks, foam); required when <paramref name="Coverage"/> is
/// positive.</param>
/// <param name="Seed">The pattern's seed.</param>
/// <param name="Lane">The render lane (<see cref="SdfLanes"/>) whose value scales <paramref name="Velocity"/> at shading
/// time; null keeps the constant. A slot lane reads zero on a surface with no slot, so a flow driven by one stands still
/// there.</param>
/// <param name="Frame">The frame <paramref name="Velocity"/> is authored in.</param>
/// <param name="CoverageLane">The render lane (<see cref="SdfLanes"/>) whose value, saturated to [0, 1], scales
/// <paramref name="Coverage"/> at shading time; null keeps the constant.</param>
public sealed record SdfFlow(Vector3 Velocity, float Scale = 1f, float Stretch = 1f, float Cycle = 1f, float Tilt = 0f,
    float Coverage = 0f, float Softness = 0.1f, SdfSurface? Surface = null, uint Seed = 0, int? Lane = null,
    SdfFlowFrame Frame = SdfFlowFrame.Object, int? CoverageLane = null) {
    /// <summary>The period, in seconds, the host reduces the flow clock by (<c>SdfWorldPackage.FlowClock</c> carries the
    /// presented time over this period, in [-1/2, 1/2]). A power of two, so a whole number of cycles fills it and the
    /// layer joins without a seam where the clock wraps.</summary>
    public const double ClockPeriodSeconds = 4096d;
    /// <summary>The shortest cycle: the clock's float precision keeps a cycle phase within about a thousandth of a cycle
    /// at most <c>ClockPeriodSeconds / MinCycle</c> cycles a period.</summary>
    public const float MinCycle = 0.25f;

    /// <summary>Gets the whole number of cycles the clock's period holds, the cycle rounded to the nearest that divides
    /// it: what the material row carries and the kernel multiplies the clock by.</summary>
    public float CyclesPerClock => MathF.Max(x: 1f, y: MathF.Round(x: (float)(ClockPeriodSeconds / Cycle)));

    /// <summary>Gets whether the flow is admissible: a finite velocity, positive finite scale, a stretch of at least
    /// one, a cycle in range, a non-negative tilt, a coverage and softness in [0, 1], a valid surface whenever coverage
    /// is positive, and each lane absent or one of <see cref="SdfLanes"/>.</summary>
    public bool IsValid =>
        VectorFunctions.IsFinite(vector: Velocity) &&
        float.IsFinite(f: Scale) && (Scale > 0f) &&
        float.IsFinite(f: Stretch) && (Stretch >= 1f) &&
        float.IsFinite(f: Cycle) && (Cycle >= MinCycle) && (Cycle <= ClockPeriodSeconds) &&
        float.IsFinite(f: Tilt) && (Tilt >= 0f) &&
        float.IsFinite(f: Coverage) && (Coverage >= 0f) && (Coverage <= 1f) &&
        float.IsFinite(f: Softness) && (Softness >= 0f) && (Softness <= 1f) &&
        ((Coverage <= 0f) || (Surface is { } surface && SdfMaterialLayers.IsSurface(surface: surface))) &&
        SdfLanes.IsLane(lane: Lane) && SdfLanes.IsLane(lane: CoverageLane) &&
        Enum.IsDefined(value: Frame);

    /// <summary>Returns the flow clock the pass block carries at a presented time: the time in seconds reduced by
    /// <see cref="ClockPeriodSeconds"/>, over that period, in [-1/2, 1/2].</summary>
    /// <param name="seconds">The presented time integrated from engine tick zero and reduced by the period
    /// (<c>PresentedTick.Integrate(1, ClockPeriodSeconds)</c>).</param>
    /// <returns>The clock.</returns>
    public static float Clock(double seconds) => (float)(seconds / ClockPeriodSeconds);
}
