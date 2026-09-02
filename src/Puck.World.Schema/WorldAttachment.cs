using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The <c>attachment</c> section — the world's ONE climb/grapple authoring surface: a body-conforming SURFACE mode
/// (climb) and a distance-CAP TETHER mode (grapple), selected by the same authored <see cref="AttachChannel"/> press
/// (climb wins when a climbable surface sits within <see cref="ClimbReach"/>; otherwise the body's aim tries a
/// grapple anchor within <see cref="GrappleMaxDistance"/>/<see cref="GrappleAssistHalfAngleDegrees"/>). ABSENT
/// (<see cref="Absent"/>) resolves to <see cref="Enabled"/> <see langword="false"/> — a world authoring nothing here
/// grants no attachment at all, today's behavior, unchanged.
/// </summary>
/// <param name="Enabled">Whether the whole surface is live. <see langword="false"/> makes every other field inert —
/// a body's attach/detach/reel channels (even if separately declared and bound) never reach the attachment state
/// machine.</param>
/// <param name="DefaultGrip">The world-level climb policy every solid placement's compiled surface(s) inherit absent
/// a per-placement <see cref="WorldPlacementGrip"/> override: <see langword="true"/> climbs everything,
/// <see langword="false"/> (the default) climbs nothing.</param>
/// <param name="ClimbReach">The non-negative world-unit radius a climb attach searches for the nearest climbable
/// surface within, from the body's own position.</param>
/// <param name="ClimbSpeed">The non-negative world-units-per-second a climbing body moves along its gripped
/// surface's tangent plane.</param>
/// <param name="GripCost">The non-negative authored cost-per-second a climbing body accrues — read back
/// (<c>body.attachment</c>) for a future economy hook; it spends no resource channel on its own today.</param>
/// <param name="GrappleMaxDistance">The non-negative world-unit ceiling a grapple aim searches along the body's
/// facing direction — also the tether's rope length at attach (the resolved anchor's actual distance, always within
/// this ceiling).</param>
/// <param name="GrappleAssistHalfAngleDegrees">The non-negative aim-assist cone half-angle, degrees, a grapple
/// candidate's bearing must fall within around the body's facing.</param>
/// <param name="ReelRate">The non-negative world-units-per-second the held <see cref="ReelChannel"/> reels the rope
/// at — positive reels out, negative in (the channel's own sign selects direction; this is a magnitude).</param>
/// <param name="ReelInFloor">The non-negative rope-length floor a reel-in clamps to.</param>
/// <param name="ReleaseMomentumScale">The non-negative multiplier detach applies to the body's velocity at the
/// instant of release — 1 preserves it exactly, below 1 dampens, above 1 boosts.</param>
/// <param name="AttachChannel">The declared channel name (validated) whose rising edge attempts an attach — climb
/// first, then grapple. <see langword="null"/> leaves attach unreachable from any channel.</param>
/// <param name="DetachChannel">The declared channel name (validated) whose rising edge clears whichever mode is
/// active. <see langword="null"/> leaves detach unreachable from any channel.</param>
/// <param name="ReelChannel">The declared channel name (validated) whose held bipolar value drives the grapple rope
/// length every tick (meaningless while climbing or unattached). <see langword="null"/> leaves reel inert.</param>
public sealed record WorldAttachmentSection(
    bool Enabled,
    bool DefaultGrip,
    float ClimbReach,
    float ClimbSpeed,
    float GripCost,
    float GrappleMaxDistance,
    float GrappleAssistHalfAngleDegrees,
    float ReelRate,
    float ReelInFloor,
    float ReleaseMomentumScale,
    string? AttachChannel = null,
    string? DetachChannel = null,
    string? ReelChannel = null
) {
    /// <summary>Gets the inert absence — disabled, every numeric field zeroed except a unit release scale, no
    /// channel bound. The behavior-preserving default for a world authoring no <c>attachment</c> section.</summary>
    public static WorldAttachmentSection Absent { get; } = new(
        Enabled: false,
        DefaultGrip: false,
        ClimbReach: 0f,
        ClimbSpeed: 0f,
        GripCost: 0f,
        GrappleMaxDistance: 0f,
        GrappleAssistHalfAngleDegrees: 0f,
        ReelRate: 0f,
        ReelInFloor: 0f,
        ReleaseMomentumScale: 1f
    );
}
/// <summary>The one-time fixed-point compilation of <see cref="WorldAttachmentSection"/> — every world-unit and
/// degree field quantized to <see cref="FixedQ4816"/>, and every declared channel name resolved to the ordinal
/// <see cref="Puck.Physics.Motion.BodyMotionOp"/>-free WorldBody attachment code reads directly (the same
/// resolved-outside/consumed-as-ordinal pattern <c>FixedWorldKit.SprintChannelOrdinal</c> uses).</summary>
public readonly record struct FixedWorldAttachment(
    bool Enabled,
    bool DefaultGrip,
    FixedQ4816 ClimbReach,
    FixedQ4816 ClimbSpeed,
    FixedQ4816 GripCost,
    FixedQ4816 GrappleMaxDistance,
    FixedQ4816 GrappleAssistHalfAngle,
    FixedQ4816 ReelRate,
    FixedQ4816 ReelInFloor,
    FixedQ4816 ReleaseMomentumScale,
    int AttachChannelOrdinal,
    // The attach/detach channels' own declared binary threshold — captured here at compile time because WorldBody
    // never resolves these two ordinals through a kit's action table (see WorldBody.Attachment.cs), so it never
    // otherwise learns the world's own per-ordinal threshold the way a kit-bound channel does
    // (FixedWorldKit.ActionThresholds, populated only for ordinals a kit's Actions map or a held-read facet like
    // SprintChannel actually claims). Reel needs no threshold — it is read continuously, never edge-tested.
    FixedQ4816 AttachThreshold,
    int DetachChannelOrdinal,
    FixedQ4816 DetachThreshold,
    int ReelChannelOrdinal
) {
    /// <summary>Gets the inert compiled absence — every WorldBody attachment read is a no-op against this value
    /// (every ordinal <c>-1</c>, every threshold zero).</summary>
    public static FixedWorldAttachment Absent { get; } = new(
        Enabled: false,
        DefaultGrip: false,
        ClimbReach: FixedQ4816.Zero,
        ClimbSpeed: FixedQ4816.Zero,
        GripCost: FixedQ4816.Zero,
        GrappleMaxDistance: FixedQ4816.Zero,
        GrappleAssistHalfAngle: FixedQ4816.Zero,
        ReelRate: FixedQ4816.Zero,
        ReelInFloor: FixedQ4816.Zero,
        ReleaseMomentumScale: FixedQ4816.One,
        AttachChannelOrdinal: -1,
        AttachThreshold: FixedQ4816.Zero,
        DetachChannelOrdinal: -1,
        DetachThreshold: FixedQ4816.Zero,
        ReelChannelOrdinal: -1
    );

    private static int ResolveOrdinal(string? name, WorldChannelTable channels) => (((name is { Length: > 0 }) && channels.TryGetOrdinal(
        name: name,
        ordinal: out var ordinal
    ))
        ? ordinal
        : -1
    );

    /// <summary>Compiles the authored section to fixed point, resolving its three channel names against the
    /// world's already-compiled channel table. Returns <see cref="Absent"/> whole when the section is disabled —
    /// every downstream read is then a single-field branch rather than three separate ordinal checks.</summary>
    public static FixedWorldAttachment Compile(WorldAttachmentSection section, WorldChannelTable channels) {
        if (!section.Enabled) {
            return Absent;
        }

        var attachOrdinal = ResolveOrdinal(
            channels: channels,
            name: section.AttachChannel
        );
        var detachOrdinal = ResolveOrdinal(
            channels: channels,
            name: section.DetachChannel
        );

        return new FixedWorldAttachment(
            AttachChannelOrdinal: attachOrdinal,
            AttachThreshold: ((attachOrdinal >= 0)
                ? channels.Threshold(ordinal: attachOrdinal)
                : FixedQ4816.Zero
            ),
            ClimbReach: FixedQ4816.FromDouble(value: section.ClimbReach),
            ClimbSpeed: FixedQ4816.FromDouble(value: section.ClimbSpeed),
            DefaultGrip: section.DefaultGrip,
            DetachChannelOrdinal: detachOrdinal,
            DetachThreshold: ((detachOrdinal >= 0)
                ? channels.Threshold(ordinal: detachOrdinal)
                : FixedQ4816.Zero
            ),
            Enabled: true,
            GrappleAssistHalfAngle: FixedQ4816.FromDouble(value: (section.GrappleAssistHalfAngleDegrees * (Math.PI / 180.0))),
            GrappleMaxDistance: FixedQ4816.FromDouble(value: section.GrappleMaxDistance),
            GripCost: FixedQ4816.FromDouble(value: section.GripCost),
            ReelChannelOrdinal: ResolveOrdinal(
                channels: channels,
                name: section.ReelChannel
            ),
            ReelInFloor: FixedQ4816.FromDouble(value: section.ReelInFloor),
            ReelRate: FixedQ4816.FromDouble(value: section.ReelRate),
            ReleaseMomentumScale: FixedQ4816.FromDouble(value: section.ReleaseMomentumScale)
        );
    }
}
