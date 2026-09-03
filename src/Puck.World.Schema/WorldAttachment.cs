using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The <c>attachment</c> section — the world's grapple authoring surface: a distance-cap tether a body's aim throws
/// at an anchor within <see cref="GrappleMaxDistance"/>/<see cref="GrappleAssistHalfAngleDegrees"/> on the authored
/// <see cref="AttachChannel"/> press. Absent (<see cref="Absent"/>) resolves to <see cref="Enabled"/>
/// <see langword="false"/> — a world authoring nothing here grants no attachment at all. Surface holds are not
/// authored here: they are a kit's own ordered <see cref="WorldMotion.Holds"/> list.
/// </summary>
/// <param name="Enabled">Whether the whole surface is live. <see langword="false"/> makes every other field inert —
/// a body's attach/detach/reel channels (even if separately declared and bound) never reach the attachment state
/// machine.</param>
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
/// <param name="AttachChannel">The declared channel name (validated) whose rising edge throws the grapple.
/// <see langword="null"/> leaves attach unreachable from any channel.</param>
/// <param name="DetachChannel">The declared channel name (validated) whose rising edge clears an active tether.
/// <see langword="null"/> leaves detach unreachable from any channel.</param>
/// <param name="ReelChannel">The declared channel name (validated) whose held bipolar value drives the grapple rope
/// length every tick. <see langword="null"/> leaves reel inert.</param>
public sealed record WorldAttachmentSection(
    bool Enabled,
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
        GrappleMaxDistance: 0f,
        GrappleAssistHalfAngleDegrees: 0f,
        ReelRate: 0f,
        ReelInFloor: 0f,
        ReleaseMomentumScale: 1f
    );
}
/// <summary>The one-time fixed-point compilation of <see cref="WorldAttachmentSection"/> — every world-unit and
/// degree field quantized to <see cref="FixedQ4816"/>, and every declared channel name resolved to the ordinal
/// WorldBody attachment code reads directly (the same resolved-outside/consumed-as-ordinal pattern
/// <c>FixedWorldKit.SprintChannelOrdinal</c> uses).</summary>
public readonly record struct FixedWorldAttachment(
    bool Enabled,
    FixedQ4816 GrappleMaxDistance,
    FixedQ4816 GrappleAssistHalfAngle,
    FixedQ4816 ReelRate,
    FixedQ4816 ReelInFloor,
    FixedQ4816 ReleaseMomentumScale,
    int AttachChannelOrdinal,
    // The attach/detach channels' own declared binary threshold — captured at compile time because WorldBody never
    // resolves these two ordinals through a kit's action table (see WorldBody.Attachment.cs), so it never otherwise
    // learns the world's own per-ordinal threshold the way a kit-bound channel does (FixedWorldKit.ActionThresholds,
    // populated only for ordinals a kit's Actions map or a held-read facet like SprintChannel actually claims). Reel
    // needs no threshold — it is read continuously, never edge-tested.
    FixedQ4816 AttachThreshold,
    int DetachChannelOrdinal,
    FixedQ4816 DetachThreshold,
    int ReelChannelOrdinal
) {
    /// <summary>Gets the inert compiled absence — every WorldBody attachment read is a no-op against this value
    /// (every ordinal <c>-1</c>, every threshold zero).</summary>
    public static FixedWorldAttachment Absent { get; } = new(
        Enabled: false,
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

    /// <summary>Compiles the authored section to fixed point, resolving its three channel names against the world's
    /// already-compiled channel table.</summary>
    /// <param name="section">The authored section.</param>
    /// <param name="channels">The world's compiled channel table.</param>
    /// <returns>The compiled policy, or <see cref="Absent"/> whole when the section is disabled.</returns>
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
            DetachChannelOrdinal: detachOrdinal,
            DetachThreshold: ((detachOrdinal >= 0)
                ? channels.Threshold(ordinal: detachOrdinal)
                : FixedQ4816.Zero
            ),
            Enabled: true,
            GrappleAssistHalfAngle: FixedQ4816.FromDouble(value: (section.GrappleAssistHalfAngleDegrees * (Math.PI / 180.0))),
            GrappleMaxDistance: FixedQ4816.FromDouble(value: section.GrappleMaxDistance),
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
