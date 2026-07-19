namespace Puck.World.Protocol;

/// <summary>The coarse capability verbs a <see cref="WorldGrant"/> confers — the closed set the server checks a
/// submission's <see cref="WorldPrincipal"/> against at each write boundary. A genre world arrives as different DATA
/// (new subjects, new sections), never a new capability.</summary>
internal enum WorldCapability : byte {
    /// <summary>The right to DRIVE a body — submit its per-tick intents and authority commands. Checked at the intent
    /// drain and <c>ApplyCommand</c>.</summary>
    Drive,

    /// <summary>The right to CONTROL a screen/machine surface — the engagement route (a player's intent diverts to the
    /// screen's machine). Checked on the engage path.</summary>
    Control,

    /// <summary>The right to MUTATE a world-document section — apply a <see cref="WorldMutation"/> targeting it.
    /// Checked at mutation apply (and, over every section, at a whole-document swap or journal undo).</summary>
    Mutate,

    /// <summary>The right to EDIT a player-profile section — apply a <c>SetPlayerSection</c> (the <c>profile.save</c>
    /// fold path). Checked against the CONCRETE <see cref="GrantSubject.Profile"/> subject of the edited profile;
    /// granted over <see cref="GrantSubject.All"/> by the permissive local defaults, so local play is unchanged until
    /// someone narrows the trust to named profiles.</summary>
    Edit,
}

/// <summary>The world-document sections the 2a <see cref="WorldMutation"/> vocabulary targets — the stable-id subject a
/// <see cref="WorldCapability.Mutate"/> grant scopes to. A section names a coarse row set; a mutation is checked against
/// exactly one.</summary>
internal enum WorldSection : byte {
    /// <summary>The locomotion kit rows, the default seat kit, and the kit→entity assignment policy.</summary>
    Kits,

    /// <summary>The diegetic screen rows.</summary>
    Screens,

    /// <summary>The placeable camera rows.</summary>
    Cameras,

    /// <summary>The static scene (ground albedos + boulders).</summary>
    Scene,

    /// <summary>The seat spawn-point list.</summary>
    Spawns,

    /// <summary>The profileless locomotion/jump tuning.</summary>
    Motion,

    /// <summary>The wander tuning.</summary>
    Wander,

    /// <summary>The census defaults (document-only).</summary>
    Population,

    /// <summary>The render-lever defaults and quality-preset table (document-only).</summary>
    Render,

    /// <summary>The data-side addon descriptor rows.</summary>
    Addons,

    /// <summary>The per-world binding overlays — targeted by the <see cref="WorldMutation.UpsertBindingOverlay"/> /
    /// <see cref="WorldMutation.RemoveBindingOverlay"/> mutations.</summary>
    Bindings,

    /// <summary>The creation ASSET rows — inline-canonical <c>puck.creation.v1</c> documents with pinned hashes.</summary>
    Creations,

    /// <summary>The placement INSTANCE rows — creations stamped into the world by reference.</summary>
    Placements,

    /// <summary>The editor/authoring policy row — headroom, placement scale envelope, candidate targeting,
    /// the sole-editor layout split, and the drag-preview deadline (see <see cref="WorldAuthoringDefaults"/>).</summary>
    Authoring,

    /// <summary>The placeable speaker rows (the audio arc) — targeted by <see cref="WorldMutation.UpsertSpeaker"/> /
    /// <see cref="WorldMutation.RemoveSpeaker"/>.</summary>
    Speakers,

    /// <summary>The tune ASSET rows — inline-canonical <c>puck.audio.v1</c> documents with pinned hashes.</summary>
    Tunes,

    /// <summary>The synth-patch ASSET rows — inline-canonical <c>puck.synth.v1</c> documents with pinned hashes.</summary>
    Patches,

    /// <summary>The audio host-section defaults (master gain, attenuation coalescing, the listener policy).</summary>
    Audio,
}

/// <summary>Which flavor of subject a <see cref="GrantSubject"/> addresses.</summary>
internal enum GrantSubjectKind : byte {
    /// <summary>The wildcard — the capability over every subject of its natural domain.</summary>
    All,

    /// <summary>A single body, by 0-based entity index.</summary>
    Body,

    /// <summary>A single screen, by engine screen index.</summary>
    Screen,

    /// <summary>A single world-document section.</summary>
    Section,

    /// <summary>A single player profile, by its stable string id (<see cref="GrantSubject.Id"/>).</summary>
    Profile,
}

/// <summary>The typed target a <see cref="WorldGrant"/> scopes to — a wildcard, a body, a screen, a document section,
/// or a player profile. A zero-alloc value key into the grant table's per-capability subject sets: profile ids are
/// strings, so the subject matches <see cref="WorldPrincipal"/>'s shape (an index lane plus a nullable string lane;
/// record-struct equality covers both).</summary>
/// <param name="Kind">The subject flavor.</param>
/// <param name="Value">The 0-based body/screen index, or the <see cref="WorldSection"/> ordinal for a section; zero for
/// <see cref="GrantSubjectKind.All"/> and <see cref="GrantSubjectKind.Profile"/>.</param>
/// <param name="Id">The profile id for <see cref="GrantSubjectKind.Profile"/>; <see langword="null"/> otherwise.</param>
internal readonly record struct GrantSubject(GrantSubjectKind Kind, int Value, string? Id = null) {
    /// <summary>The wildcard subject — the capability over its whole domain.</summary>
    public static GrantSubject All { get; } = new(Kind: GrantSubjectKind.All, Value: 0);

    /// <summary>A single body by 0-based entity index.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public static GrantSubject Body(int index) => new(Kind: GrantSubjectKind.Body, Value: index);

    /// <summary>A single screen by engine screen index.</summary>
    /// <param name="index">The engine screen index.</param>
    public static GrantSubject Screen(int index) => new(Kind: GrantSubjectKind.Screen, Value: index);

    /// <summary>A single world-document section.</summary>
    /// <param name="section">The section.</param>
    public static GrantSubject Section(WorldSection section) => new(Kind: GrantSubjectKind.Section, Value: (int)section);

    /// <summary>A single player profile by its stable string id.</summary>
    /// <param name="id">The profile id.</param>
    public static GrantSubject Profile(string id) => new(Kind: GrantSubjectKind.Profile, Value: 0, Id: id);

    /// <summary>A short stable label for console echoes — <c>all</c>, <c>body:&lt;n&gt;</c>, <c>screen:&lt;n&gt;</c>,
    /// <c>section:&lt;name&gt;</c>, <c>profile:&lt;id&gt;</c>.</summary>
    /// <returns>The label.</returns>
    public string Describe() => Kind switch {
        GrantSubjectKind.All => "all",
        GrantSubjectKind.Body => $"body:{Value}",
        GrantSubjectKind.Screen => $"screen:{Value}",
        GrantSubjectKind.Section => $"section:{((WorldSection)Value).ToString().ToLowerInvariant()}",
        GrantSubjectKind.Profile => $"profile:{Id}",
        _ => "?",
    };
}

/// <summary>One grant row — the wire payload of <c>world.grant</c>/<c>world.revoke</c>: a principal holds a capability
/// over a subject, optionally EXCLUSIVE (the engagement latch generalized — acquiring an exclusive grant a live holder
/// owns is rejected). Revoke ignores <see cref="Exclusive"/>.</summary>
/// <param name="Principal">The acting identity the grant is for.</param>
/// <param name="Capability">The capability conferred.</param>
/// <param name="Subject">The subject the capability scopes to.</param>
/// <param name="Exclusive">Whether the grant is held exclusively (single holder per capability+subject).</param>
internal readonly record struct WorldGrant(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject, bool Exclusive);
