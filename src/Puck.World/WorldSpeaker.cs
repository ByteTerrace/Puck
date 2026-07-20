using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Authoring;

namespace Puck.World;

/// <summary>
/// The signal a <see cref="WorldSpeaker"/>'s feed taps — a SHARED source identity, never an inline
/// payload: the runtime drains each distinct source once per mix block and every feed tapping it shares that one
/// pull, so "stereo = two rows sharing a source" costs one drain. The <c>$type</c> string is the JSON discriminator,
/// matching <see cref="WorldScreenSource"/>'s convention; a new source kind is a new derived record plus its
/// <see cref="JsonDerivedTypeAttribute"/> line.
/// </summary>
[JsonDerivedType(typeof(WorldSpeakerSource.None), typeDiscriminator: "none")]
[JsonDerivedType(typeof(WorldSpeakerSource.Machine), typeDiscriminator: "machine")]
[JsonDerivedType(typeof(WorldSpeakerSource.Tune), typeDiscriminator: "tune")]
[JsonDerivedType(typeof(WorldSpeakerSource.Synth), typeDiscriminator: "synth")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal abstract record WorldSpeakerSource {
    private WorldSpeakerSource() {
    }

    /// <summary>No signal is bound — honest silence (the emitter holds its place; <c>audio.emitters</c> reads the
    /// state).</summary>
    internal sealed record None() : WorldSpeakerSource;

    /// <summary>A live screen-hosted machine's audio, identified by screen slot — screen index IS machine identity
    /// for screen-hosted machines. The validator checks only that the screen row EXISTS, never that its declared
    /// source is <c>$type machine</c> (runtime inserts overlay declared sources); no live machine at drain time is
    /// silence plus a state echo, never a reject.</summary>
    /// <param name="ScreenIndex">The declared <see cref="WorldScreen.Index"/> whose hosted machine feeds this source.</param>
    internal sealed record Machine(int ScreenIndex) : WorldSpeakerSource;

    /// <summary>A tune asset (<see cref="WorldTune"/>) played through a headless machine host — acquired while any
    /// speaker references it, released when orphaned (a runtime derivation, never a data concept).</summary>
    /// <param name="TuneId">The referenced <see cref="WorldTune.Id"/> (must resolve).</param>
    internal sealed record Tune(string TuneId) : WorldSpeakerSource;

    /// <summary>The world voice synth playing a patch asset (<see cref="WorldPatch"/>). Patches are MONO by
    /// construction: the feed's channel selectors degenerate to <c>mix</c> — documented, never rejected.</summary>
    /// <param name="PatchId">The referenced <see cref="WorldPatch.Id"/> (must resolve).</param>
    internal sealed record Synth(string PatchId) : WorldSpeakerSource;
}

/// <summary>A speaker's feed — WHAT it plays: a shared source identity, a stereo channel selector,
/// and a gain. Stereo separation is two independent speaker rows sharing one source with <c>left</c>/<c>right</c>
/// selectors and different geometry — no group/attachment construct. Mono sources (the synth) degenerate every
/// selector to <see cref="ChannelMix"/>.</summary>
/// <param name="Source">The shared source identity this feed taps.</param>
/// <param name="Channel">The stereo channel selector — <see cref="ChannelMix"/>, <see cref="ChannelLeft"/>, or
/// <see cref="ChannelRight"/>.</param>
/// <param name="Gain">The feed gain (1 = unity), bounded by <see cref="CreationSoundDocument.MaxLevel"/> — the one
/// audio gain ceiling the validator enforces everywhere.</param>
internal sealed record WorldSpeakerFeed(WorldSpeakerSource Source, string Channel, float Gain) {
    /// <summary>The mix selector — the average of both source channels.</summary>
    public const string ChannelMix = "mix";
    /// <summary>The left-channel selector.</summary>
    public const string ChannelLeft = "left";
    /// <summary>The right-channel selector.</summary>
    public const string ChannelRight = "right";
}

/// <summary>A point speaker's distance-attenuation policy, or <see langword="null"/> on the row to coalesce to the
/// <see cref="WorldAudioDefaults"/> section (<c>DefaultSpeakerRadius</c>/<c>DefaultCurve</c>).</summary>
/// <param name="Radius">The finite audible support radius in world units — at or beyond it the emitter is CULLED
/// (finite support IS the cull).</param>
/// <param name="Curve">The falloff curve token (<see cref="WorldAudioDefaults.CurveSmoothstep"/> is v1's whole
/// vocabulary), or <see langword="null"/> for the audio-defaults curve.</param>
internal sealed record WorldSpeakerAttenuation(float Radius, string? Curve);

/// <summary>
/// One placeable speaker in the world — the camera family's sibling: abstract, name-keyed,
/// <c>$type</c>-discriminated, whole-row <c>UpsertSpeaker</c>/<c>RemoveSpeaker</c>. Routing chiasmus: screens
/// consume cameras; speakers consume audio sources. Omni-only v1 (no cones — stereo separation is already the feed
/// model). A new speaker kind is a new derived record plus its <see cref="JsonDerivedTypeAttribute"/> line.
/// </summary>
/// <param name="Name">The speaker's stable name — its mutation address.</param>
/// <param name="Feed">The feed it plays (source + channel + gain).</param>
/// <param name="Attenuation">The point-attenuation policy, or <see langword="null"/> to coalesce to the
/// <see cref="WorldAudioDefaults"/> section. Beds carry their own radii instead and leave this null.</param>
[JsonDerivedType(typeof(WorldSpeaker.Fixed), typeDiscriminator: "fixed")]
[JsonDerivedType(typeof(WorldSpeaker.Anchored), typeDiscriminator: "anchored")]
[JsonDerivedType(typeof(WorldSpeaker.Bed), typeDiscriminator: "bed")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal abstract record WorldSpeaker(
    string Name,
    WorldSpeakerFeed Feed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSpeakerAttenuation? Attenuation
) {
    /// <summary>A speaker posed directly in world space.</summary>
    /// <param name="Name">The speaker's stable name.</param>
    /// <param name="Position">The emitter position, world space.</param>
    /// <param name="Feed">The feed it plays.</param>
    /// <param name="Attenuation">The attenuation policy, or <see langword="null"/> for the audio defaults.</param>
    internal sealed record Fixed(string Name, Vector3 Position, WorldSpeakerFeed Feed, WorldSpeakerAttenuation? Attenuation = null)
        : WorldSpeaker(Name: Name, Feed: Feed, Attenuation: Attenuation);

    /// <summary>A speaker anchored to a <see cref="WorldAnchor"/> — the entity/leaf/placement pose it rides supplies
    /// the live position; <paramref name="Offset"/> is the exact attachment point in the anchor's local axes on top
    /// of that. Unlike cameras, speakers resolve EVERY anchor kind, placements included (the placement's stamped
    /// transform is reachable client-side from the definition plus the animator state).</summary>
    /// <param name="Name">The speaker's stable name.</param>
    /// <param name="Anchor">What the speaker rides (see <see cref="WorldAnchor"/>).</param>
    /// <param name="Offset">The attachment point relative to the anchor's resolved pose, in anchor-local axes.</param>
    /// <param name="Feed">The feed it plays.</param>
    /// <param name="Attenuation">The attenuation policy, or <see langword="null"/> for the audio defaults.</param>
    internal sealed record Anchored(string Name, WorldAnchor Anchor, Vector3 Offset, WorldSpeakerFeed Feed, WorldSpeakerAttenuation? Attenuation = null)
        : WorldSpeaker(Name: Name, Feed: Feed, Attenuation: Attenuation);

    /// <summary>An ambient bed — a region PRESENCE, not a position: center-panned, its gain an envelope of the listener's distance from
    /// <paramref name="Center"/> — full inside <paramref name="InnerRadius"/>, zero at <paramref name="Radius"/> —
    /// with its presence slew bounded by <paramref name="FadeSeconds"/>. Box extents become a later <c>$type</c>
    /// only if a sphere proves dishonest.</summary>
    /// <param name="Name">The speaker's stable name.</param>
    /// <param name="Center">The region's extent center, world space.</param>
    /// <param name="Radius">The region's outer radius — the envelope's zero and the cull edge.</param>
    /// <param name="InnerRadius">The full-presence inner radius (null = 0 — the envelope shoulders from the center).</param>
    /// <param name="FadeSeconds">The presence slew bound in seconds (null = the audio defaults'
    /// <c>DefaultBedFadeSeconds</c>).</param>
    /// <param name="Feed">The feed it plays.</param>
    internal sealed record Bed(
        string Name,
        Vector3 Center,
        float Radius,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? InnerRadius,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? FadeSeconds,
        WorldSpeakerFeed Feed
    ) : WorldSpeaker(Name: Name, Feed: Feed, Attenuation: null);
}

/// <summary>
/// One tune ASSET row — a whole <c>puck.audio.v1</c> document embedded INLINE-CANONICAL with its identity hash
/// pinned beside it (the same hash-pin/canonicalize contract as <see cref="WorldCreation"/>): the compose boundary
/// canonicalizes on upsert through <see cref="AudioCanonicalizer"/> and rejects a hash the pipeline did not itself compute; the
/// validator re-verifies the pin on every candidate; <c>world.save</c> re-canonicalizes. The hash doubles as the
/// runtime restart discriminator: a content change restarts the tune's headless host, a rename does not.
/// </summary>
/// <param name="Id">The row's stable string id — its mutation address and the handle a speaker's
/// <see cref="WorldSpeakerSource.Tune"/> references.</param>
/// <param name="Document">The canonical (validated + normalized) audio document.</param>
/// <param name="Hash">The SHA-256 hex64 of the document's canonical bytes.</param>
internal sealed record WorldTune(string Id, AudioDocument Document, string Hash);

/// <summary>
/// One synth-patch ASSET row — a whole <c>puck.synth.v1</c> document embedded INLINE-CANONICAL with its identity
/// hash pinned beside it via <see cref="SynthPatchCanonicalizer"/>, the same pin/restart contract as
/// <see cref="WorldCreation"/>; see <see cref="WorldTune"/> for the shared pin/restart semantics.
/// </summary>
/// <param name="Id">The row's stable string id — its mutation address; referenced by
/// <see cref="WorldSpeakerSource.Synth"/> and by <see cref="WorldEmission.PatchId"/> facets.</param>
/// <param name="Document">The canonical (validated + normalized) synth patch document.</param>
/// <param name="Hash">The SHA-256 hex64 of the document's canonical bytes.</param>
internal sealed record WorldPatch(string Id, SynthPatchDocument Document, string Hash);

/// <summary>An emission FACET — a synth voice a world row itself makes (phenomena sound like
/// themselves; a creek is not a speaker). Nullable on <see cref="WorldSceneRow"/> and <see cref="WorldPlacement"/>
/// — a facet edit is the row's existing whole-row upsert. Under a repeat facet the
/// emission binds to the placement ROOT only (an 8×8 lattice must not become 64 voices; a per-copy flag is a future
/// facet field, not a schema fork).</summary>
/// <param name="PatchId">The referenced <see cref="WorldPatch.Id"/> (must resolve).</param>
/// <param name="Level">The emitter level (1 = unity), bounded by <see cref="CreationSoundDocument.MaxLevel"/>.</param>
/// <param name="Radius">The audible support radius in world units, or <see langword="null"/> for the audio
/// defaults' <c>DefaultSpeakerRadius</c>.</param>
internal sealed record WorldEmission(string PatchId, float Level, float? Radius = null);

/// <summary>
/// One world-event → sound binding — a row of the Audio section's CUE TABLE: when the named engine
/// event fires, the referenced patch voices through a short-lived TRANSIENT emitter placed per <see cref="Placement"/>.
/// Event tokens are a CLOSED, published vocabulary of engine MECHANISMS (<see cref="EventTokens"/>): a genre ships
/// different cue rows; new tokens appear only when the engine grows new mechanisms.
/// </summary>
/// <param name="Event">The event token (must be one of <see cref="EventTokens"/>).</param>
/// <param name="PatchId">The referenced <see cref="WorldPatch.Id"/> the cue voices (must resolve).</param>
/// <param name="GainThousandths">The cue's voice gain in thousandths (1000 = unity), or <see langword="null"/> for
/// unity. Bounded by <see cref="CreationSoundDocument.MaxLevel"/> × 1000 — the shared audio gain ceiling in the cue
/// table's integer unit.</param>
/// <param name="Placement">Where the cue sounds: <see cref="PlacementAtSite"/> (spatial, at the event's world
/// position — the shimmer's audio twin; events with no derivable site fall back to the listener),
/// <see cref="PlacementListener"/> (UI feedback — rides the listener pose, so distance 0 renders full gain and the
/// mixer's on-top-of-listener pan hold centers it), or <c>emitter:&lt;name&gt;</c> (sounds from the named speaker's
/// resolved pose and support radius).</param>
internal sealed record WorldAudioCue(
    string Event,
    string PatchId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GainThousandths,
    string Placement
) {
    /// <summary>The spatial placement token — the cue sounds at the event's world position.</summary>
    public const string PlacementAtSite = "at-site";
    /// <summary>The listener placement token — the cue rides the listener pose (centered, full gain).</summary>
    public const string PlacementListener = "listener";
    /// <summary>The named-speaker placement prefix (<c>emitter:&lt;speaker-name&gt;</c>).</summary>
    public const string PlacementEmitterPrefix = "emitter:";

    /// <summary>A world mutation applied (the edit-echo lane — fired beside the loud accept line).</summary>
    public const string MutationApplied = "mutation.applied";
    /// <summary>A world mutation rejected (validator/guard/capacity — never a grant denial).</summary>
    public const string MutationRejected = "mutation.rejected";
    /// <summary>A capability denial — a mutate attempt without its grant, or a refused grant acquisition.</summary>
    public const string GrantDenied = "grant.denied";
    /// <summary>A local seat avatar's jump takeoff. RESERVED: no producer is wired yet (the client view carries no
    /// grounded/airborne signal; deriving one from Y heuristics would misfire on flying/swimming kits).</summary>
    public const string PlayerJump = "player.jump";
    /// <summary>A local seat avatar's landing. RESERVED (see <see cref="PlayerJump"/>).</summary>
    public const string PlayerLand = "player.land";
    /// <summary>A local seat avatar's footstep — derived from the presentation gait phase (one footfall per half
    /// gait cycle; distance-driven, so an idle avatar is silent). Presentation-side by design.</summary>
    public const string PlayerFootstep = "player.footstep";
    /// <summary>A machine booted onto a screen slot (the binder lifecycle — <c>screen.insert</c> and the
    /// reconcile-driven declared-source boot).</summary>
    public const string ScreenBoot = "screen.boot";
    /// <summary>A machine boot/lifecycle fault on a screen slot (missing content, unresolved engine).</summary>
    public const string ScreenFault = "screen.fault";
    /// <summary>A local seat joined the roster.</summary>
    public const string SeatJoin = "seat.join";

    /// <summary>The CLOSED cue-event vocabulary, the same closed-list discipline as
    /// <see cref="WorldAvatarCatalog.HumanoidAnchorRoles"/>: the validator rejects any token outside it, and this
    /// list IS the published contract — new tokens appear only
    /// when the engine grows new mechanisms, never per feature.</summary>
    public static readonly IReadOnlyList<string> EventTokens = [
        MutationApplied,
        MutationRejected,
        GrantDenied,
        PlayerJump,
        PlayerLand,
        PlayerFootstep,
        ScreenBoot,
        ScreenFault,
        SeatJoin,
    ];

    /// <summary>Whether <paramref name="token"/> is one of the published <see cref="EventTokens"/>.</summary>
    /// <param name="token">The candidate token.</param>
    public static bool IsEventToken(string? token) {
        foreach (var candidate in EventTokens) {
            if (string.Equals(a: candidate, b: token, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The world's audio host-section defaults — document defaults with the same absence-coalesce convention every
/// defaults section uses (see <see cref="WorldStorageDefaults"/>): absent-in-JSON coalesces to <see cref="Default"/>.
/// These are document data, not editor policy; live master volume is the <c>world.volume</c> session lever: the lever
/// owns "now" once touched, <see cref="MasterGain"/> owns boot, and <c>world.save</c> folds the lever back into
/// <see cref="MasterGain"/> (the render-levers asymmetry).
/// </summary>
/// <param name="MasterGain">The master gain the mix path applies over every emitter (1 = unity), bounded by
/// <see cref="CreationSoundDocument.MaxLevel"/>.</param>
/// <param name="DefaultSpeakerRadius">The audible support radius a point emitter coalesces to when its row declares
/// no attenuation/radius.</param>
/// <param name="DefaultCurve">The falloff curve a point emitter coalesces to (<see cref="CurveSmoothstep"/> is v1's
/// whole vocabulary — the mixer's squared-smoothstep finite-support law).</param>
/// <param name="DefaultBedFadeSeconds">The presence slew bound a bed coalesces to when it declares none.</param>
/// <param name="Listener">The listener policy: <see cref="ListenerFocus"/> (the active view
/// camera's pose listens), <c>seat:&lt;n&gt;</c> (that seat's view camera), or a declared camera name — so a stage
/// or museum world can pin its listener without touching the runtime.</param>
/// <param name="Cues">THE CUE TABLE (default empty): world events tied to sound as data —
/// see <see cref="WorldAudioCue"/>.</param>
internal sealed record WorldAudioDefaults(
    float MasterGain,
    float DefaultSpeakerRadius,
    string DefaultCurve,
    float DefaultBedFadeSeconds,
    string Listener,
    IReadOnlyList<WorldAudioCue> Cues
) {
    private readonly IReadOnlyList<WorldAudioCue> m_cues = (Cues ?? []);

    /// <summary>The cue table. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="MotionTuning.Response"/>'s does.</summary>
    public IReadOnlyList<WorldAudioCue> Cues {
        get => m_cues;
        init => m_cues = (value ?? []);
    }

    /// <summary>The focus listener policy token — the active view camera's pose listens.</summary>
    public const string ListenerFocus = "focus";
    /// <summary>The seat listener policy prefix (<c>seat:1</c>..<c>seat:4</c>).</summary>
    public const string ListenerSeatPrefix = "seat:";
    /// <summary>The squared-smoothstep finite-support falloff — v1's entire curve vocabulary.</summary>
    public const string CurveSmoothstep = "smoothstep";

    /// <summary>The built-in audio defaults: unity master, an 8-unit speaker radius, the smoothstep curve, a
    /// half-second bed fade, the focus listener.</summary>
    public static WorldAudioDefaults Default { get; } = new WorldAudioDefaults(
        MasterGain: 1f,
        DefaultSpeakerRadius: 8f,
        DefaultCurve: CurveSmoothstep,
        DefaultBedFadeSeconds: 0.5f,
        Listener: ListenerFocus,
        Cues: []
    );
}
