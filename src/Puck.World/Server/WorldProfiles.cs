using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The world's live player catalog: the runtime <see cref="WorldProfile"/> handles compiled from the loaded
/// <see cref="WorldPlayerDocument"/>, the machine-local boot seat (<see cref="LastUsed"/>, kept in a sidecar so it never
/// roams), the document <see cref="Revision"/> (the ordering key), and the save path back to storage. Participants
/// reference these handles by identity, so a settings mutation is picked up live; a rarely-run mutation (create/set/
/// reseat/a folded rebind) persists synchronously (human-cadence actions, not a per-frame path).
/// </summary>
/// <remarks>Single-threaded: every mutator runs on the command-pump thread during a verb's handler or a session-request
/// apply, never concurrently with the frame source's read, so no lock guards this state.</remarks>
internal sealed class WorldProfiles {
    private readonly List<WorldProfile> m_profiles;
    private readonly WorldProfileStore m_store;
    private string m_lastUsedId;
    private long m_revision;
    private long m_lastSyncedRevision;
    private string m_updatedAtUtc;
    private string? m_versionToken;
    private bool m_lastPreconditionFailed;

    /// <summary>Initializes a new instance of the <see cref="WorldProfiles"/> class from a loaded document and boot
    /// seat.</summary>
    /// <param name="document">The loaded (or reseeded/migrated) player document to view.</param>
    /// <param name="lastUsedId">The machine-local boot-seat profile id (from the sidecar).</param>
    /// <param name="lastSyncedRevision">The last-synced revision cursor (0 with no cloud wired).</param>
    /// <param name="versionToken">The catalog blob's storage version token at load, or <see langword="null"/>.</param>
    /// <param name="store">The store to persist mutations back through.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldProfiles(WorldPlayerDocument document, string lastUsedId, long lastSyncedRevision, string? versionToken, WorldProfileStore store) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: lastUsedId);
        ArgumentNullException.ThrowIfNull(argument: store);

        m_lastUsedId = lastUsedId;
        m_revision = document.Revision;
        m_lastSyncedRevision = lastSyncedRevision;
        m_updatedAtUtc = document.UpdatedAtUtc;
        m_versionToken = versionToken;
        m_store = store;
        m_profiles = new List<WorldProfile>(capacity: document.Profiles.Count);

        // Drop hand-edited duplicate ids/names, keeping the first — Find/BootProfile resolve to the first, so a later
        // same-key entry would orphan yet still cycle in the picker. (The thick validator rejects these at the boundary;
        // this is the runtime belt-and-braces for a document that slipped past it.)
        var seenIds = new HashSet<string>(comparer: StringComparer.Ordinal);
        var seenNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var profile in document.Profiles) {
            if (seenIds.Add(item: profile.Id) && seenNames.Add(item: profile.Identity.Name)) {
                m_profiles.Add(item: new WorldProfile(profile: profile));
            } else {
                Console.Error.WriteLine(value: $"[profiles] dropped duplicate profile '{profile.Identity.Name}' (id '{profile.Id}') from {store.FilePath}; keeping the first.");
            }
        }

        if (m_profiles.Count == 0) {
            Console.Error.WriteLine(value: $"[profiles] stored catalog is empty; reseeding the built-in default at {store.FilePath}.");

            foreach (var profile in WorldPlayerDocument.BuildDefault().Profiles) {
                m_profiles.Add(item: new WorldProfile(profile: profile));
            }

            Save();
        }
    }

    /// <summary>The stored profiles, in catalog order (the order a candidate picker cycles through).</summary>
    public IReadOnlyList<WorldProfile> All => m_profiles;

    /// <summary>The profile id player 1 seats on at boot (machine-local; sidecar-persisted).</summary>
    public string LastUsed => m_lastUsedId;

    /// <summary>The document revision (the ordering key) — bumped on every save.</summary>
    public long Revision => m_revision;

    /// <summary>The last-synced revision cursor — the highest revision a cloud sync has confirmed. Stays 0 while
    /// no cloud is wired.</summary>
    public long LastSyncedRevision => m_lastSyncedRevision;

    /// <summary>Whether the local catalog is ahead of the cloud — the DERIVED sync state (<see cref="Revision"/> &gt;
    /// <see cref="LastSyncedRevision"/>), never a volatile flag. With no cloud wired this is always true (the honest
    /// truth: the local copy has never synced).</summary>
    public bool Dirty => (m_revision > m_lastSyncedRevision);

    /// <summary>The last-persist wall-clock stamp (the Revision tiebreak) — bumped on every save.</summary>
    public string UpdatedAtUtc => m_updatedAtUtc;

    /// <summary>The catalog blob's storage version token as of the last read/write (the clobber-guard input a
    /// cloud-backed store's if-match rides), or <see langword="null"/> when unavailable.</summary>
    public string? VersionToken => m_versionToken;

    /// <summary>Whether the last save was refused by an if-match precondition (the clobber guard fired). Always false on
    /// the local backend (unconditional last-writer-wins); the field a cloud-backed store's conditional writes
    /// set.</summary>
    public bool LastPreconditionFailed => m_lastPreconditionFailed;

    /// <summary>The on-disk path of the player document (for the "edit this file" diagnostics).</summary>
    public string FilePath => m_store.FilePath;

    /// <summary>Finds a profile by name (case-insensitive), or <see langword="null"/> when none matches.</summary>
    /// <param name="name">The profile name to look up.</param>
    /// <returns>The matching profile, or <see langword="null"/>.</returns>
    public WorldProfile? Find(string name) {
        foreach (var profile in m_profiles) {
            if (string.Equals(a: profile.Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return profile;
            }
        }

        return null;
    }

    /// <summary>Finds a profile by stable id (case-sensitive), or <see langword="null"/> when none matches.</summary>
    /// <param name="id">The profile id to look up.</param>
    /// <returns>The matching profile, or <see langword="null"/>.</returns>
    public WorldProfile? FindById(string id) {
        foreach (var profile in m_profiles) {
            if (string.Equals(a: profile.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return profile;
            }
        }

        return null;
    }

    /// <summary>The boot profile for player 1: the <see cref="LastUsed"/> id when it still resolves, else the first
    /// catalog entry (a document that lost its boot seat still wakes the game playable).</summary>
    public WorldProfile BootProfile => (FindById(id: m_lastUsedId) ?? m_profiles[0]);

    /// <summary>Creates a new profile with the given name and color, persisting the catalog. The name must be unique
    /// (case-insensitive); a duplicate returns <see langword="null"/> and changes nothing.</summary>
    /// <param name="name">The unique profile name (used verbatim as the stable id).</param>
    /// <param name="colorHex">The body color as a <c>#RRGGBB</c> hex string.</param>
    /// <returns>The created profile, or <see langword="null"/> when the name is already taken.</returns>
    public WorldProfile? Create(string name, string colorHex) {
        if ((Find(name: name) is not null) || (FindById(id: name) is not null)) {
            return null;
        }

        var profile = new WorldProfile(profile: new WorldPlayerProfile(
            Id: name,
            Identity: new WorldPlayerIdentity(Name: name, Color: colorHex),
            Motion: new WorldPlayerMotion()
        ));

        m_profiles.Add(item: profile);
        Save();

        return profile;
    }

    /// <summary>Reseats player 1's boot profile by id (persisted to the local sidecar so the next boot wakes on the same
    /// identity). A no-roam machine-local write, separate from the catalog document.</summary>
    /// <param name="id">The profile id to seat on at boot.</param>
    public void SetLastUsed(string id) {
        m_lastUsedId = id;
        m_store.SaveLocal(local: new WorldPlayerLocal(LastUsedId: id, LastSyncedRevision: m_lastSyncedRevision));
    }

    /// <summary>Durably edits ONE section of a profile (the server's <c>SetPlayerSection</c> apply): parse the section
    /// payload, validate a CANDIDATE catalog through <see cref="WorldPlayerDocumentValidator"/> (so an identity rename
    /// stays unique, colors parse, speeds are finite-positive, and a binding section compiles — the one thick gate, not
    /// a second bespoke check), then mutate the shared live handle and persist. On any parse/validation failure nothing
    /// changes and <paramref name="reason"/> carries why. All four <see cref="WorldPlayerSection"/> kinds are real; a
    /// malformed payload rejects loudly.</summary>
    /// <param name="id">The profile id to edit.</param>
    /// <param name="section">Which section the payload targets.</param>
    /// <param name="payload">The section value as JSON text.</param>
    /// <param name="reason">The failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the section applied.</returns>
    public bool ApplySection(string id, WorldPlayerSection section, string payload, out string reason) {
        reason = string.Empty;

        if (FindById(id: id) is not { } profile) {
            reason = $"no profile with id '{id}'";

            return false;
        }

        switch (section) {
            case WorldPlayerSection.Identity:
                if (!WorldPlayerJson.TryParseIdentity(payload: payload, identity: out var identity, error: out var identityError)) {
                    reason = $"identity payload did not parse ({identityError})";

                    return false;
                }

                if (!ValidateEdit(profile: profile, edited: profile.ToProfile() with { Identity = identity }, reason: out reason)) {
                    return false;
                }

                // The live handle update — a seated participant renders Color off this handle, so refreshing it here is
                // what keeps couch co-op non-stale (the server also refreshes the population's cached body color).
                profile.SetIdentity(name: identity.Name, colorHex: identity.Color);

                break;
            case WorldPlayerSection.Motion:
                if (!WorldPlayerJson.TryParseMotion(payload: payload, motion: out var motion, error: out var motionError)) {
                    reason = $"motion payload did not parse ({motionError})";

                    return false;
                }

                if (!ValidateEdit(profile: profile, edited: profile.ToProfile() with { Motion = motion }, reason: out reason)) {
                    return false;
                }

                profile.MoveSpeed = motion.MoveSpeed;
                profile.TurnSpeed = motion.TurnSpeed;
                profile.InvertLookX = motion.InvertLookX;

                break;
            case WorldPlayerSection.Bindings:
                if (!WorldPlayerJson.TryParseBindings(payload: payload, document: out var bindings, error: out var bindingError)) {
                    reason = $"bindings payload did not parse ({bindingError})";

                    return false;
                }

                // The candidate validator gates a non-null binding section through BindingProfile.Compile itself (the
                // same composed-with-default compile), so no separate compile check is duplicated here.
                if (!ValidateEdit(profile: profile, edited: profile.ToProfile() with { Bindings = bindings }, reason: out reason)) {
                    return false;
                }

                profile.Bindings = bindings;

                break;
            case WorldPlayerSection.Preferences:
                if (!WorldPlayerJson.TryParsePreferences(payload: payload, preferences: out var preferences, error: out var preferencesError)) {
                    reason = $"preferences payload did not parse ({preferencesError})";

                    return false;
                }

                // The preferences bag is the open extension store (the validator does not gate it); the parsed object
                // replaces the whole bag — the section-edit grain, consistent with identity/motion.
                profile.Preferences = preferences;

                break;
            default:
                reason = $"unknown player section '{section}'";

                return false;
        }

        Save();

        return true;
    }

    // Validate a candidate catalog with ONE profile's section swapped for the edited body, through the one thick gate.
    // Composing the whole document is what makes an identity rename honest (its name/color are checked for uniqueness
    // against every OTHER profile, exactly as a load would); a non-null binding section is compiled by the validator.
    private bool ValidateEdit(WorldProfile profile, WorldPlayerProfile edited, out string reason) {
        var candidateProfiles = new List<WorldPlayerProfile>(capacity: m_profiles.Count);

        foreach (var existing in m_profiles) {
            candidateProfiles.Add(item: (ReferenceEquals(objA: existing, objB: profile) ? edited : existing.ToProfile()));
        }

        var candidate = new WorldPlayerDocument(
            Schema: WorldPlayerDocument.SchemaVersion,
            Revision: m_revision,
            UpdatedAtUtc: m_updatedAtUtc,
            Profiles: candidateProfiles
        );

        return WorldPlayerDocumentValidator.TryValidate(document: candidate, reason: out reason);
    }

    /// <summary>Snapshots the live catalog into a serializable document (the <c>GetPlayerDocument</c> read-back and the
    /// persistence source of truth).</summary>
    /// <returns>The current document at the live revision.</returns>
    public WorldPlayerDocument ToDocument() {
        return new WorldPlayerDocument(
            Schema: WorldPlayerDocument.SchemaVersion,
            Revision: m_revision,
            UpdatedAtUtc: m_updatedAtUtc,
            Profiles: m_profiles.Select(selector: static profile => profile.ToProfile()).ToList()
        );
    }

    /// <summary>Persists the live catalog back to storage synchronously, bumping the document <see cref="Revision"/> and
    /// stamping <see cref="UpdatedAtUtc"/>. Called after every catalog mutation (create, a live <c>profile.set</c>, a
    /// folded rebind). Captures the storage version token the write returns so <c>storage.status</c> echoes it.</summary>
    public void Save() {
        m_revision++;
        m_updatedAtUtc = WorldPlayerDocument.StampNow();

        var document = new WorldPlayerDocument(
            Schema: WorldPlayerDocument.SchemaVersion,
            Revision: m_revision,
            UpdatedAtUtc: m_updatedAtUtc,
            Profiles: m_profiles.Select(selector: static profile => profile.ToProfile()).ToList()
        );

        // A locked/unwritable player.json (antivirus, cloud-sync hold, read-only file) must not kill the session: the
        // live catalog stays authoritative in memory. A genuine logic bug (not malformed input) still escapes.
        try {
            var result = m_store.SaveAsync(document: document).AsTask().GetAwaiter().GetResult();

            m_lastPreconditionFailed = result.PreconditionFailed;

            if (result.VersionToken is { } token) {
                m_versionToken = token;
            }
        } catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
            Console.Error.WriteLine(value: $"[profiles] could not save to {m_store.FilePath} ({exception.Message}); the change stays live in memory.");
        }
    }
}
