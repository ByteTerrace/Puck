using Puck.Maths;

namespace Puck.World;

/// <summary>
/// A kit's tether facet: an aimed distance-cap rope a body attaches to a surface point along its own facing,
/// reels, and detaches — the mechanism behind a grapple, a tow line, or any other rope-bound kit (<c>body.attach</c>/
/// <c>body.detach</c>/<c>body.reel</c>). Presence is the whole switch, the same convention <see cref="WorldRigid"/>
/// and <see cref="WorldCarry"/> carry for their own facets: a kit authoring no <c>tether</c> row refuses every one of
/// those channels by name. Surface holds are not authored here: they are a kit's own ordered
/// <see cref="WorldMotion.Holds"/> list.
/// </summary>
/// <param name="MaxAnchorDistance">The non-negative, fixed-representable world-unit ceiling an attach aim searches along the body's
/// facing direction — also the tether's rope length at attach (the resolved anchor's actual distance, always within
/// this ceiling).</param>
/// <param name="AimHalfAngleDegrees">The fixed-representable aim-assist cone half-angle, degrees, within <c>[0, 180]</c>, an attach
/// candidate's bearing must fall within around the body's facing. Honoured only by a collider-list contact
/// provider; a field provider's own directed march has no candidate list to score bearings over and ignores it
/// (see <c>WorldSolidField.TryNearestSurfaceAlongDirection</c>'s remarks).</param>
/// <param name="LengthRate">The non-negative, fixed-representable world-units-per-second the held <see cref="ReelChannel"/> reels the
/// rope at — positive reels out, negative in (the channel's own sign selects direction; this is a magnitude).</param>
/// <param name="MinLength">The non-negative, fixed-representable rope-length floor a reel-in clamps to.</param>
/// <param name="ReleaseVelocityScale">The non-negative, fixed-representable multiplier a detach applies to the body's velocity at the
/// instant of release — 1 (the default) preserves it exactly, below 1 dampens, above 1 boosts.</param>
/// <param name="AttachChannel">The declared channel name (validated) whose rising edge attaches the tether.
/// <see langword="null"/> leaves attach unreachable from any channel.</param>
/// <param name="DetachChannel">The declared channel name (validated) whose rising edge clears an active tether.
/// <see langword="null"/> leaves detach unreachable from any channel.</param>
/// <param name="ReelChannel">The declared channel name (validated) whose held bipolar value drives the rope length
/// every tick. <see langword="null"/> leaves reel inert.</param>
/// <param name="ModeState">The declared <c>state.body</c>/<c>state.identity</c> counter slot name this facet writes
/// <c>1</c> while attached and <c>0</c> otherwise — the camera program's <c>select</c> op keys off it exactly as it
/// keys off any other <c>state.&lt;row&gt;</c> value. <see langword="null"/> writes nothing.</param>
public sealed record WorldTether(
    float MaxAnchorDistance,
    float AimHalfAngleDegrees,
    float LengthRate,
    float MinLength,
    float ReleaseVelocityScale = 1f,
    string? AttachChannel = null,
    string? DetachChannel = null,
    string? ReelChannel = null,
    string? ModeState = null
);
/// <summary>The one-time fixed-point compilation of a kit's <see cref="WorldTether"/> facet — every world-unit and
/// degree field quantized to <see cref="FixedQ4816"/>, and every declared channel/state name resolved to the ordinal
/// <c>WorldBody</c> reads directly (the same resolved-outside/consumed-as-ordinal pattern
/// <c>FixedSpeed.HeldOrdinal</c> uses).</summary>
public readonly record struct FixedWorldTether(
    FixedQ4816 MaxAnchorDistance,
    FixedQ4816 AimHalfAngle,
    FixedQ4816 LengthRate,
    FixedQ4816 MinLength,
    FixedQ4816 ReleaseVelocityScale,
    int AttachChannelOrdinal,
    // The attach/detach channels' own declared binary threshold — captured at compile time because WorldBody never
    // resolves these two ordinals through a kit's action table, so it never otherwise learns the world's own
    // per-ordinal threshold the way a kit-bound channel does (FixedWorldKit.ActionThresholds, populated only for
    // ordinals a kit's Actions map or a held-read facet like Speed.Held actually claims). Reel needs no threshold —
    // it is read continuously, never edge-tested.
    FixedQ4816 AttachThreshold,
    int DetachChannelOrdinal,
    FixedQ4816 DetachThreshold,
    int ReelChannelOrdinal,
    // -1 when the facet writes no mode-state row.
    int ModeStateOrdinal
) {
    private static int ResolveChannel(string? name, WorldChannelTable channels) => (((name is { Length: > 0 }) && channels.TryGetOrdinal(
        name: name,
        ordinal: out var ordinal
    ))
        ? ordinal
        : -1
    );

    /// <summary>Compiles an authored facet to fixed point, resolving its channel names against the world's
    /// already-compiled channel table and its <see cref="WorldTether.ModeState"/> name against the kit's already-
    /// compiled action-state register file.</summary>
    /// <param name="tether">The authored facet, or <see langword="null"/> for a kit that carries none.</param>
    /// <param name="channels">The world's compiled channel table.</param>
    /// <param name="stateOrdinals">The kit's compiled action-state slot ordinals, keyed by declared name.</param>
    /// <returns>The compiled facet, or <see langword="null"/> when <paramref name="tether"/> is
    /// <see langword="null"/>.</returns>
    public static FixedWorldTether? Compile(WorldTether? tether, WorldChannelTable channels, IReadOnlyDictionary<string, int> stateOrdinals) {
        if (tether is not { } facet) {
            return null;
        }

        var attachOrdinal = ResolveChannel(
            channels: channels,
            name: facet.AttachChannel
        );
        var detachOrdinal = ResolveChannel(
            channels: channels,
            name: facet.DetachChannel
        );
        var modeStateOrdinal = (((facet.ModeState is { Length: > 0 } modeState) && stateOrdinals.TryGetValue(
            key: modeState,
            value: out var slot
        ))
            ? slot
            : -1
        );

        return new FixedWorldTether(
            AimHalfAngle: FixedQ4816.FromDouble(value: (facet.AimHalfAngleDegrees * (Math.PI / 180.0))),
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
            LengthRate: FixedQ4816.FromDouble(value: facet.LengthRate),
            MaxAnchorDistance: FixedQ4816.FromDouble(value: facet.MaxAnchorDistance),
            MinLength: FixedQ4816.FromDouble(value: facet.MinLength),
            ModeStateOrdinal: modeStateOrdinal,
            ReelChannelOrdinal: ResolveChannel(
                channels: channels,
                name: facet.ReelChannel
            ),
            ReleaseVelocityScale: FixedQ4816.FromDouble(value: facet.ReleaseVelocityScale)
        );
    }
}
