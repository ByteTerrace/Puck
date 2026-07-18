using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The serializable root of a user's player-scoped state (<c>puck.world.player.v1</c>): a CATALOG of profiles every
/// participant seats on, carried with a monotonic <see cref="Revision"/> so cloud sync can order two copies (§2.5) and
/// an <see cref="Extensions"/> bag so unknown sections survive a round-trip (the data-side plugin posture). It absorbs
/// and retires <c>puck.world.profiles.v1</c>: the store migrates the old file once at load, then deletes it.
/// </summary>
/// <remarks>Persisted through the reflection-based <c>JsonObjectBlobStore</c> (Web defaults), so every member is a
/// property of a primitive/string/nested-record type — no <see cref="System.Numerics.Vector3"/> field STJ would
/// silently zero without <c>IncludeFields</c> (the <see cref="WorldPlayerIdentity"/> color is a hex string for exactly
/// this reason). Machine-local boot seating (which profile player 1 wakes on) does NOT live here — it must not roam to
/// the cloud — but in a small local-only sidecar (<see cref="WorldPlayerLocal"/>).</remarks>
/// <param name="Schema">The document schema tag; <see cref="SchemaVersion"/> for a well-formed document.</param>
/// <param name="Revision">The monotonic save counter (§2.5 ordering key) — bumped on every persist. Starts at 1. It
/// ORDERS two copies; it never guards against a clobber (that is the storage version token's job — the two are
/// deliberately separate mechanisms, §6.4).</param>
/// <param name="UpdatedAtUtc">The wall-clock instant of the last persist (ISO-8601 round-trip "O"), the Revision
/// tiebreak for the cloud arc. Persistence sits OUTSIDE the sim-determinism contract, so wall clock is legal here (§2.5).</param>
/// <param name="Profiles">The stored profile catalog; ids and names are each unique (case-insensitive on names).</param>
internal sealed record WorldPlayerDocument(
    string Schema,
    long Revision,
    string UpdatedAtUtc,
    IReadOnlyList<WorldPlayerProfile> Profiles
) {
    /// <summary>The schema version this build authors and accepts. A stored document whose schema differs is reseeded in
    /// place (the on-disk file must never lie about what a profile means).</summary>
    public const string SchemaVersion = "puck.world.player.v1";

    /// <summary>The <see cref="UpdatedAtUtc"/> the built-in default carries until its first real persist — the Unix
    /// epoch in ISO-8601 round-trip form, a deterministic seed value (never a live wall-clock read at construction).</summary>
    public const string DefaultUpdatedAtUtc = "1970-01-01T00:00:00.0000000+00:00";

    /// <summary>The wall-clock stamp for a fresh persist — <see cref="DateTimeOffset.UtcNow"/> in ISO-8601 round-trip
    /// form. Persistence is outside the sim-determinism contract (§2.5), so a real clock read is legal here.</summary>
    /// <returns>The current UTC instant as an ISO-8601 round-trip string.</returns>
    public static string StampNow() {
        return DateTimeOffset.UtcNow.ToString(format: "O", formatProvider: System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Unknown sections preserved across a round-trip — the data-side extensibility posture (the
    /// <see cref="Puck.Scene.PuckRunDocument"/> precedent). A settable accessor is required: STJ appends to it during
    /// deserialization.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Builds the built-in default document: four color-named profiles matching the classic avatar palette,
    /// engine-default bindings (null), <see cref="Revision"/> 1.</summary>
    /// <returns>A fresh default document.</returns>
    public static WorldPlayerDocument BuildDefault() {
        return new WorldPlayerDocument(
            Schema: SchemaVersion,
            Revision: 1L,
            UpdatedAtUtc: DefaultUpdatedAtUtc,
            Profiles: [
                new WorldPlayerProfile(Id: "amber", Identity: new WorldPlayerIdentity(Name: "amber", Color: "#ED8530"), Motion: new WorldPlayerMotion()),
                new WorldPlayerProfile(Id: "cobalt", Identity: new WorldPlayerIdentity(Name: "cobalt", Color: "#3373D9"), Motion: new WorldPlayerMotion()),
                new WorldPlayerProfile(Id: "moss", Identity: new WorldPlayerIdentity(Name: "moss", Color: "#4CBD52"), Motion: new WorldPlayerMotion()),
                new WorldPlayerProfile(Id: "violet", Identity: new WorldPlayerIdentity(Name: "violet", Color: "#A359D6"), Motion: new WorldPlayerMotion()),
            ]
        );
    }
}

/// <summary>
/// One catalog entry: a stable <see cref="Id"/> (its mutation/edit address, never a display value), the
/// <see cref="Identity"/> and <see cref="Motion"/> sections, an optional binding profile (<see langword="null"/> = the
/// engine default, the common case that keeps documents small and inherits default evolution), and an
/// <see cref="Extensions"/> preferences bag (the open, forward-compatible per-profile store — no second dictionary).
/// </summary>
/// <param name="Id">The stable profile id (unique within the catalog).</param>
/// <param name="Identity">The display identity (name + color).</param>
/// <param name="Motion">The locomotion preferences.</param>
/// <param name="Bindings">The profile's binding overrides (layered over the engine default and world overlays), or
/// <see langword="null"/> to inherit the engine default unchanged.</param>
internal sealed record WorldPlayerProfile(
    string Id,
    WorldPlayerIdentity Identity,
    WorldPlayerMotion Motion,
    BindingProfileDocument? Bindings = null
) {
    /// <summary>The open, forward-compatible preferences bag — arbitrary named per-profile values a data-only addon or
    /// a future setting can carry, surviving a round-trip untouched. A settable accessor is required: STJ appends to it
    /// during deserialization.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>One profile's display identity — the name shown on screen and over the pipe, and the body color.</summary>
/// <remarks>The color is a <c>#RRGGBB</c> hex string, not a <see cref="System.Numerics.Vector3"/>: the reflection
/// serializer silently zeroes struct fields without <c>IncludeFields</c>, so hex sidesteps the trap.</remarks>
/// <param name="Name">The unique (case-insensitive) profile name — the participant's identity.</param>
/// <param name="Color">The body color as a <c>#RRGGBB</c> hex string (persisted verbatim).</param>
internal sealed record WorldPlayerIdentity(string Name, string Color);

/// <summary>One profile's locomotion preferences — the three settings the console mutates live.</summary>
/// <param name="MoveSpeed">Locomotion speed in world units per second.</param>
/// <param name="TurnSpeed">Turn speed in radians per second.</param>
/// <param name="InvertLookX">Whether the look-stick X axis is inverted at consumption.</param>
internal sealed record WorldPlayerMotion(float MoveSpeed = 4f, float TurnSpeed = 2.5f, bool InvertLookX = false);

/// <summary>
/// The small machine-LOCAL sidecar beside the player document (<c>local.json</c>): boot-seating and sync-cursor state
/// that must NOT roam to the cloud. It carries which profile player 1 wakes on plus the last-synced revision cursor,
/// kept a separate blob so the cloud arc syncs the roaming <see cref="WorldPlayerDocument"/> without dragging per-machine
/// state along.
/// </summary>
/// <param name="LastUsedId">The <see cref="WorldPlayerProfile.Id"/> player 1 seats on at boot.</param>
/// <param name="LastSyncedRevision">The highest document <see cref="WorldPlayerDocument.Revision"/> a cloud sync has
/// confirmed uploaded (§2.5.1). Sync state is DERIVED — <c>document.Revision &gt; LastSyncedRevision</c> means dirty —
/// never a volatile flag, so it is crash-safe. Stays 0 while no cloud is wired, so <c>storage.status</c> reports the
/// local copy as unsynced (the honest truth this arc).</param>
internal sealed record WorldPlayerLocal(string LastUsedId, long LastSyncedRevision = 0L);

/// <summary>
/// The catalog-level projection persisted at the per-user container's <c>world/player.json</c> — the document minus the
/// profile bodies, which live in one <c>world/profiles/&lt;id&gt;.json</c> blob each (§2.5.3). Splitting the store this
/// way makes two devices' edits to DIFFERENT profiles independent (the same address model the cloud uses); the catalog
/// carries the ordering key and the ordered id list a load re-assembles the document from.
/// </summary>
/// <param name="Schema">The document schema tag, mirroring <see cref="WorldPlayerDocument.Schema"/>.</param>
/// <param name="Revision">The document revision (§2.5 ordering key).</param>
/// <param name="UpdatedAtUtc">The last-persist wall-clock stamp (the Revision tiebreak).</param>
/// <param name="ProfileIds">The ordered profile ids — the catalog order a load walks to read each profile blob.</param>
internal sealed record WorldPlayerCatalog(
    string Schema,
    long Revision,
    string UpdatedAtUtc,
    IReadOnlyList<string> ProfileIds
) {
    /// <summary>The document-level unknown sections, carried on the catalog blob so a round-trip preserves them
    /// (a settable accessor is required — STJ appends to it during deserialization).</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}
