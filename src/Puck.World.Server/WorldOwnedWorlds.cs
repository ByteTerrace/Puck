using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The owned world documents available as player identities.</summary>
/// <remarks>Every id in this catalog addresses exactly one storage location, and that is an INVARIANT of the catalog
/// — no longer a courtesy of whoever put a document in it: every id here is a <see cref="WorldSafeName"/>, so
/// <see cref="WorldOwnedWorldFileName"/>'s mapping is INJECTIVE by construction and two distinct ids can never
/// collapse onto one file name. The type is what makes an id unique here — two identities can no more share a file
/// than they can share an id — enforced at the earliest door a candidate string crosses: the document's own JSON
/// parse for an authored seed or a loaded/pulled document, or <see cref="Create"/>'s console-verb argument.</remarks>
public sealed class WorldOwnedWorlds {
    private static readonly WorldCellName MoveSpeedState = WorldCellName.Parse(candidate: "identity-move-speed");
    private static readonly WorldCellName TurnSpeedState = WorldCellName.Parse(candidate: "identity-turn-speed");
    private readonly List<WorldIdentity> m_identities;
    private readonly string m_directory;
    private readonly WorldDefinition m_template;
    private readonly WorldMotionDefaults m_motion;
    private long m_revision = 1;
    private WorldDocumentSubmissionReceipt? m_lastReceipt;

    /// <summary>Loads owned worlds from a directory, seeding authored identities when it is empty.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/> or whitespace.</exception>
    public WorldOwnedWorlds(WorldDefinition template, string directory, Guid machineId, IWorldNeighbourResolver? neighbours = null) {
        ArgumentNullException.ThrowIfNull(argument: template);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        m_template = IdentityBase(fallback: template);
        m_motion = template.Motion;
        m_directory = directory;
        MachineId = machineId;
        Defaults = template.PlayerDefaults;
        Directory.CreateDirectory(path: directory);
        m_identities = [];

        // Ordinal file order, so which member of a colliding set is admitted is a property of the names rather than of
        // the directory enumeration's whim.
        var paths = Directory.GetFiles(path: directory, searchPattern: $"*{WorldOwnedWorldFileName.Suffix}", searchOption: SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        var present = new HashSet<string>(collection: paths.Select(Path.GetFileName)!, comparer: StringComparer.Ordinal);

        foreach (var path in paths) {
            // Remove only border claims still identical to the seed template, before the cross-document proof: they
            // describe the visited arena's relationship to its siblings, not this per-identity document. A
            // genuinely authored claim that differs from the seed remains and must prove itself through the
            // resolver below.
            if (WorldDefinitionFileSource.TryLoadLocally(path: path, definition: out var local, contentHash: out _, reason: out _) &&
                (local?.Identity is not null) &&
                StripInheritedBorderClaims(template: m_template, candidate: local, stripped: out var stripped)) {
                _ = WorldDefinitionSerialization.Save(definition: stripped, path: path);
                Console.Error.WriteLine(value: $"[identity] removed inherited border claims from {path}");
            }

            if (!WorldDefinitionFileSource.TryLoad(path: path, definition: out var document, contentHash: out _, reason: out var reason, neighbours: neighbours) || (document?.Identity is null)) {
                Console.Error.WriteLine(value: $"[identity] owned world refused: {reason}");

                continue;
            }

            // An owned world's file name IS its id through the one mapping every save and every cloud key derives from,
            // so a file whose name is not the one its declared id maps to is a document this catalog cannot address:
            // its next save would land on the OTHER name and silently replace whatever lives there. That single rule is
            // also what makes an id unique here — two files can no more share an id than they can share a name.
            var fileName = Path.GetFileName(path: path);
            var addressed = WorldOwnedWorldFileName.For(id: document.Identity.Id);

            if (!string.Equals(a: fileName, b: addressed, comparisonType: StringComparison.Ordinal)) {
                Console.Error.WriteLine(value: $"[identity] owned world refused: '{fileName}' declares id '{document.Identity.Id}', which this catalog stores as '{addressed}'{(present.Contains(item: addressed) ? $" — the file already holding that id" : " — a name it does not carry")}; an owned world's file name is its id, so rename the file or the id");

                continue;
            }

            m_identities.Add(item: new WorldIdentity(document: document, defaults: Defaults));
        }

        if (m_identities.Count == 0) {
            foreach (var seed in Defaults.Identities) {
                var identity = new WorldIdentity(document: Seed(template: m_template, motion: m_motion, seed: seed), defaults: Defaults);
                m_identities.Add(item: identity);
                Save(identity: identity);
            }
        }
    }

    /// <summary>Gets the identities, one per owned world.</summary>
    public IReadOnlyList<WorldIdentity> All => m_identities;
    /// <summary>Gets the visited world's player presentation defaults.</summary>
    public WorldPlayerDefaults Defaults { get; }
    /// <summary>Gets the installation id used by controller state slots.</summary>
    public Guid MachineId { get; }
    /// <summary>Gets the local owned-world mutation counter.</summary>
    public long Revision => m_revision;
    /// <summary>Gets the latest cross-document durable-state verdict, visible to both authorities.</summary>
    public WorldDocumentSubmissionReceipt? LastReceipt => m_lastReceipt;
    /// <summary>Observes every owner-side cross-document durable-state verdict for a tape.</summary>
    public Action<WorldDocumentSubmissionReceipt>? ReceiptTap { get; set; }
    /// <summary>Gets the owned-world directory.</summary>
    public string FilePath => m_directory;
    /// <summary>Gets the first owned identity used before a controller preference applies.</summary>
    public WorldIdentity BootProfile => m_identities[0];

    /// <summary>Finds an identity by display name.</summary>
    public WorldIdentity? Find(string name) => m_identities.FirstOrDefault(identity => string.Equals(a: identity.Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase));
    /// <summary>Finds an identity by owned-world id.</summary>
    public WorldIdentity? FindById(string id) => m_identities.FirstOrDefault(identity => string.Equals(a: identity.Id, b: id, comparisonType: StringComparison.Ordinal));

    /// <summary>Asks an owned world to apply one tick-stamped durable-state operation.</summary>
    public WorldDocumentSubmissionReceipt Submit(WorldDocumentSubmission submission) {
        var receipt = Decide(submission: submission);
        m_lastReceipt = receipt;
        ReceiptTap?.Invoke(obj: receipt);
        return receipt;
    }

    /// <summary>Reads one owner-granted durable slot for a visited world's tick boundary.</summary>
    public bool TryReadDurableState(string ownerId, string sourceDocumentId, string slot, ActionStateKind kind, out DurableStateValue value, out string reason) {
        value = default;
        if ((FindById(id: ownerId) is not { } owner) || (owner.Document is not { } document)) {
            reason = $"owner world '{ownerId}' is unavailable";
            return false;
        }
        var principal = WorldPrincipal.Document(id: sourceDocumentId);
        var subject = GrantSubject.State(name: slot);
        if (!document.Grants.Any(grant => grant.Principal == principal && grant.Capability == WorldCapability.Observe && grant.Subject == subject)) {
            reason = $"{principal.Describe()} has no read grant for {subject.Describe()}";
            return false;
        }
        if (!owner.TryReadState(name: slot, row: out var row)) {
            reason = $"unknown slot '{slot}'";
            return false;
        }
        switch (row, kind) {
            case ({ Kind: CellKind.Fixed, IsSlot: true } fixedRow, ActionStateKind.Counter):
                value = new DurableStateValue(Name: slot, Value: Puck.Maths.FixedQ4816.FromRawBits(value: fixedRow.Cells![0].Value), TimerTicks: 0);
                reason = string.Empty;
                return true;
            // A tick count crosses as an unsigned quantity, so a negative cell cannot be read as one. That is a
            // DIFFERENT refusal from a kind mismatch and says so: the row a caller named is the right kind and holds a
            // value this lane cannot represent. It is reachable only on a row that does NOT declare
            // WorldStateRow.NonNegative — declaring it is what makes an Int row a timer, and the write door and the
            // document validator both hold that floor.
            case ({ Kind: CellKind.Int, IsSlot: true } intRow, ActionStateKind.Timer):
                if (intRow.Cells![0].Value < 0) {
                    reason = $"{subject.Describe()} holds {intRow.Cells![0].Value}, which no tick count can carry — an int row read as a timer must declare a non-negative floor";
                    return false;
                }
                value = new DurableStateValue(Name: slot, Value: Puck.Maths.FixedQ4816.Zero, TimerTicks: checked((ulong)intRow.Cells![0].Value));
                reason = string.Empty;
                return true;
            default:
                reason = $"{subject.Describe()} has the wrong storage kind";
                return false;
        }
    }

    /// <summary>Resolves a reconnect-stable controller from state-slot references in the owned worlds.</summary>
    public WorldIdentity? PreferredProfile(InputDeviceId device) {
        if ((device.Persistence != InputDeviceIdentityPersistence.Reconnect) || (MachineId == Guid.Empty)) {
            return null;
        }
        WorldIdentity? match = null;
        foreach (var identity in m_identities) {
            foreach (var slots in identity.Document?.Identity?.Controllers ?? []) {
                if (identity.TryReadState(name: slots.MachineState, row: out var machineRow) && (machineRow is { Kind: CellKind.Text, IsSlot: true } && (machineRow.Cells![0].Text is { } machine))
                    && identity.TryReadState(name: slots.DeviceState, row: out var deviceRow) && (deviceRow is { Kind: CellKind.Text, IsSlot: true } && (deviceRow.Cells![0].Text is { } storedDevice))
                    && string.Equals(a: machine, b: MachineId.ToString("D"), comparisonType: StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a: storedDevice, b: device.Value.ToString("D"), comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    if (match is not null) {
                        return null;
                    }
                    match = identity;
                }
            }
        }
        return match;
    }

    /// <summary>Stores a controller preference as two text slots in the selected owned world.</summary>
    public void RememberPreferredController(WorldIdentity profile, InputDeviceId device) {
        if ((device.Persistence != InputDeviceIdentityPersistence.Reconnect) || (MachineId == Guid.Empty) || (profile.Document?.Identity is not { } definition)) {
            return;
        }
        var key = device.Value.ToString("N");
        var slots = new WorldControllerStateSlots(MachineState: WorldCellName.Parse(candidate: $"controller-{key}-machine"), DeviceState: WorldCellName.Parse(candidate: $"controller-{key}-device"));
        profile.WriteState(row: new WorldStateRow(Name: slots.MachineState, Kind: CellKind.Text, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Text: MachineId.ToString("D"))]));
        profile.WriteState(row: new WorldStateRow(Name: slots.DeviceState, Kind: CellKind.Text, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Text: device.Value.ToString("D"))]));
        profile.ReplaceDocument(document: profile.Document with { Identity = definition with { Controllers = [.. (definition.Controllers ?? []), slots] } });
        Save(identity: profile);
    }

    /// <summary>Creates and persists one owned world. <paramref name="name"/> is a <see cref="WorldSafeName"/> — the
    /// type already proves it addresses a file of its own, so the only thing left to refuse here is a collision: an id
    /// this catalog already holds (<c>FindById</c>) or a display name already taken (<c>Find</c>).</summary>
    /// <param name="name">The new world's id and display name.</param>
    /// <param name="colorHex">The avatar color.</param>
    /// <param name="reason">Why creation was refused, or empty on success.</param>
    /// <returns>The created identity, or <see langword="null"/> with <paramref name="reason"/> set.</returns>
    public WorldIdentity? Create(WorldSafeName name, string colorHex, out string reason) {
        if ((Find(name: name) is not null) || (FindById(id: name) is not null)) {
            reason = $"'{name}' already exists";
            return null;
        }
        var identity = new WorldIdentity(document: Seed(template: m_template, motion: m_motion, seed: new WorldIdentitySeed(Id: name, Name: name, Color: colorHex)), defaults: Defaults);
        m_identities.Add(item: identity);
        Save(identity: identity);
        reason = string.Empty;
        return identity;
    }

    /// <summary>Persists every owned world.</summary>
    public void Save() {
        foreach (var identity in m_identities) {
            Save(identity: identity);
        }
    }

    /// <summary>Adopts a pulled cloud copy of an owned world: replaces the in-memory identity that shares its id (or
    /// adds a new one), then persists it locally through the ordinary save path. The caller has already validated the
    /// document through the boot loader's gate — which means its id already survived <see cref="WorldSafeName"/>'s
    /// JSON parse — so the only thing left to refuse here is a document without an identity section at all, which
    /// cannot be an owned world.</summary>
    /// <param name="document">The validated world document to adopt.</param>
    /// <param name="reason">What happened — replaced, added, or why it was refused.</param>
    /// <returns>Whether the document was adopted.</returns>
    public bool ReplaceFromSync(WorldDefinition document, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: document);
        if (document.Identity is null) {
            reason = "document has no identity section";
            return false;
        }
        var incoming = new WorldIdentity(document: document, defaults: Defaults);
        var index = m_identities.FindIndex(candidate => string.Equals(a: candidate.Id, b: incoming.Id, comparisonType: StringComparison.Ordinal));
        if (index >= 0) {
            m_identities[index] = incoming;
            reason = "replaced the local copy";
        } else {
            m_identities.Add(item: incoming);
            reason = "added a new owned world";
        }
        Save(identity: incoming);
        return true;
    }

    private void Save(WorldIdentity identity) {
        if ((identity.Document is not { } document) || (document.Identity is not { } identitySection)) {
            return;
        }
        var path = Path.Combine(path1: m_directory, path2: WorldOwnedWorldFileName.For(id: identitySection.Id));
        _ = WorldDefinitionSerialization.Save(definition: document, path: path);
        m_revision++;
    }

    // THE CONTRACT SPLIT this door's text extension reveals: the DOOR — a grant naming
    // Principal==Document(source) && Capability==Mutate && Subject==State(slot), plus a WriteMask admitting the
    // requested WorldDocumentWriteKind — is SUBMITTER-AGNOSTIC. It answers "may THIS document write THAT slot", never
    // "who is asking on the sim's behalf". Only the SUBMITTER varies: today Server.WorldServer.Step, once per tick,
    // for numeric Counter/Timer DurableStateOutputs; the text arm below adds a second submitter (a player-initiated
    // delivery, dev-harnessed today by identity.deliver, chat-integrated in a later lane) that calls this SAME
    // Submit/Decide pair with a Text operand instead of a numeric one. Because the predicate above never branches on
    // WHO is submitting, widening the operand vocabulary here cannot muddy the numeric contract — a second door would
    // have needed its own grant/mask re-derivation and could drift from this one; extending the one shape instead
    // means there is exactly one place this predicate is ever stated.
    private WorldDocumentSubmissionReceipt Decide(WorldDocumentSubmission submission) {
        if (string.IsNullOrWhiteSpace(value: submission.SourceDocumentId)) {
            return Refuse(submission: submission, reason: "source document id is missing");
        }
        if ((FindById(id: submission.OwnerDocumentId) is not { } owner) || (owner.Document is not { } document)) {
            return Refuse(submission: submission, reason: $"owner world '{submission.OwnerDocumentId}' is unavailable");
        }
        if (string.IsNullOrWhiteSpace(value: submission.Slot)) {
            return Refuse(submission: submission, reason: "slot name is missing");
        }

        var principal = WorldPrincipal.Document(id: submission.SourceDocumentId);
        var subject = GrantSubject.State(name: submission.Slot);
        var grant = document.Grants.FirstOrDefault(candidate =>
            candidate.Principal == principal &&
            candidate.Capability == WorldCapability.Mutate &&
            candidate.Subject == subject);
        // The WRITE mask, never the kind mask: this door's vocabulary is WorldDocumentWriteKind (replace vs.
        // accumulate), and the two are distinct types precisely so this call site cannot read a mutation-kind lane
        // as an operation lane. A row with no write mask reaches nothing here — this channel's mask is REQUIRED
        // (unlike an Edit row's optional narrowing), because a foreign document's write is deny-by-default and the
        // mask is the whole of what admits it.
        if ((grant.Principal != principal) || (grant.WriteMask is not { } writes) || !writes.Contains(kind: submission.Kind)) {
            return Refuse(submission: submission, reason: $"{principal.Describe()} has no {submission.Kind.ToString().ToLowerInvariant()} grant for {subject.Describe()}");
        }
        if (!owner.TryReadState(name: submission.Slot, row: out var row) || (row is null)) {
            // Delivery into an UNDECLARED row refuses BY NAME with the remedy, never a silent drop and never a
            // spooled mailbox — the recipient's OWN document must already declare the row before an external
            // document can land anything in it. That refusal IS the honest offline-delivery boundary: no row, no
            // booted doc, no delivery; mail stays deferred rather than queued anywhere. NOTE: as of this door, an
            // owned identity's state rows are authored where its document is composed (game content) or edited
            // directly — there is no console verb yet that authors an ARBITRARY new row inside an owned identity's
            // document the way world.row.set state does for the RUNNING world's own document (identity.motion/
            // identity.hud only reach their own narrow slots). The remedy below says WHAT must be true, not HOW to
            // make it true over a console today — a gap worth closing when the whisper verb needs recipients to
            // declare their own inbox row live.
            return Refuse(submission: submission, reason: $"unknown slot '{submission.Slot}' — {subject.Describe()} must be declared in the recipient's OWN document before an external document can write into it; there is no spooled delivery for an undeclared row");
        }

        // TEXT branch — a SEPARATE arm off the SAME door, never a sibling door: the submitter varies (sim-driven
        // numeric outputs below vs. a player-initiated text delivery here), but the admission predicate above
        // (grant + write mask) already ran identically for both. Checked ahead of the numeric switch because
        // row.Kind == CellKind.Text can never match either numeric arm, so routing it here first is strictly a
        // clearer refusal, not a different gate.
        if (submission.Text is { } text) {
            if (row.Kind != CellKind.Text) {
                return Refuse(submission: submission, reason: $"{subject.Describe()} has the wrong storage kind");
            }
            // TEXT IS SET-ONLY: Add would mean "concatenate", which this door never does — silently accumulating
            // strings is concatenation-by-stealth. Checked here (not folded into the write-mask gate above) because
            // a grant CAN legitimately admit writes:Set,Add (e.g. shared with a numeric row) — the row's own shape
            // is what refuses Add for text, not the grant.
            if (submission.Kind == WorldDocumentWriteKind.Add) {
                return Refuse(submission: submission, reason: $"{subject.Describe()} is text — Add refuses by name (text is Set-only; no concatenation-by-stealth)");
            }
            if (text.Length > WorldStateCapacity.MaxTextValueLength) {
                return Refuse(submission: submission, reason: $"text is {text.Length} UTF-16 code units, past {subject.Describe()}'s {WorldStateCapacity.MaxTextValueLength}-unit cap");
            }

            // TWO admitted row shapes: a SLOT overwrites (the original, single-value durable-slot delivery); a
            // BOUNDED, EVICTING keyed row (WorldStateRow.Evicts + Capacity — e.g. a chat inbox) APPENDS instead,
            // through the SAME primitive a self-authored chat log uses (WorldIdentity.TryAppendEvictingText), so a
            // whisper landing in a bounded inbox and a player appending their own log can never disagree about
            // eviction order or key uniqueness. The appended cell's key is minted from the RECIPIENT's own document
            // (never wire-supplied), so a foreign document can never choose or collide a key.
            if (row.IsSlot) {
                owner.WriteState(row: row with { Cells = [new WorldStateCell(Key: WorldStateRow.SlotKey, Text: text)] });
            } else if (row.Evicts && (row.Capacity is not null)) {
                if (!owner.TryAppendEvictingText(rowName: row.Name, text: text, evictedKey: out _, reason: out var appendReason)) {
                    return Refuse(submission: submission, reason: $"{subject.Describe()} {appendReason}");
                }
            } else {
                return Refuse(submission: submission, reason: $"{subject.Describe()} has the wrong storage kind — a text delivery lands only on a slot or a bounded, evicting row");
            }

            Save(identity: owner);
            return new WorldDocumentSubmissionReceipt(Submission: submission, Accepted: true, Reason: "owner accepted the granted operation");
        }

        // THE ROW'S OWN DECLARED ENVELOPE, never this door's guess at one. WorldIdentity.WriteState below swaps the
        // row in with no revalidation, so whatever this admits is what the persisted document carries — and the
        // document's own validator (WorldDefinitionValidator's state walk) re-checks Min/Max AND WorldStateRow
        // .NonNegative at the owned world's next boot. A value this door admits that the validator would refuse is a
        // document that stops loading, so the two must read the SAME traits off the SAME row: the non-negative floor
        // is the row's declared NonNegative, on BOTH numeric arms, never an Int-only constant. (An Int + NonNegative
        // row is what "timer" meant before the kind vocabularies reconciled — see WorldStateRow's own remarks — so
        // reading the trait is what makes the timer floor a timer floor rather than a coincidence of the arm.)
        // The two admitted numeric pairings are decided HERE and the envelope is applied ONCE below, so a Fixed
        // counter and an Int timer cannot grow different floors, envelopes, or overflow behavior by drifting apart.
        var storageMatches = (row, submission.StorageKind) switch {
            ({ Kind: CellKind.Fixed, IsSlot: true }, ActionStateKind.Counter) => true,
            ({ Kind: CellKind.Int, IsSlot: true }, ActionStateKind.Timer) => true,
            _ => false,
        };

        if (!storageMatches) {
            return Refuse(submission: submission, reason: $"{subject.Describe()} has the wrong storage kind");
        }

        try {
            var current = row.Cells![0].Value;
            var value = ((submission.Kind == WorldDocumentWriteKind.Add) ? checked(current + submission.Value) : submission.Value);

            if (row.NonNegative && (value < 0)) {
                return Refuse(submission: submission, reason: $"value {value} is negative — {subject.Describe()}'s floor is non-negative");
            }

            if (((row.Min is { } minimum) && (value < minimum)) || ((row.Max is { } maximum) && (value > maximum))) {
                return Refuse(submission: submission, reason: $"value {value} is outside {subject.Describe()}'s authored envelope");
            }

            owner.WriteState(row: row with { Cells = [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: value)] });
        } catch (OverflowException) {
            return Refuse(submission: submission, reason: $"{subject.Describe()} overflowed");
        }

        Save(identity: owner);
        return new WorldDocumentSubmissionReceipt(Submission: submission, Accepted: true, Reason: "owner accepted the granted operation");
    }

    private static WorldDocumentSubmissionReceipt Refuse(WorldDocumentSubmission submission, string reason) => new(Submission: submission, Accepted: false, Reason: reason);

    private static WorldDefinition Seed(WorldDefinition template, WorldMotionDefaults motion, WorldIdentitySeed seed) => WithoutBorderClaims(definition: template) with {
        DocumentId = seed.Id,
        Motion = motion,
        Identity = new WorldIdentityDefinition(Id: seed.Id, Name: seed.Name, Color: seed.Color, MoveSpeedState: MoveSpeedState, TurnSpeedState: TurnSpeedState),
        State = [
            new WorldStateRow(Name: MoveSpeedState, Kind: CellKind.Fixed, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: Puck.Maths.FixedQ4816.FromDouble(value: motion.MoveSpeed).Value)]),
            new WorldStateRow(Name: TurnSpeedState, Kind: CellKind.Fixed, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: Puck.Maths.FixedQ4816.FromDouble(value: motion.TurnSpeed).Value)]),
        ],
        BindingOverlays = [],
        Hud = WorldHudSection.Default,
    };

    // An identity seed owns profile/state data, not the visited arena's cross-document ground-strip agreement. Keep
    // the portal itself (the identity document remains a faithful world-shaped document), but clear the neighbour
    // claim that can only be proven in the visited document's own storage context.
    private static WorldDefinition WithoutBorderClaims(WorldDefinition definition) => definition with {
        Placements = [.. definition.Placements.Select(selector: static placement => placement with {
            FaceSources = ((placement.FaceSources is null)
                ? null
                : [.. placement.FaceSources.Select(selector: static face => ((face.Portal?.MarginDepth is null)
                    ? face
                    : face with { Portal = face.Portal with { MarginDepth = null } }))]),
        })],
    };

    // Upgrade the old full-clone seed shape without weakening validation for a later, genuinely authored claim:
    // only a portal facet whose complete routing identity AND depth still match the current seed template is
    // inherited. Anything else remains untouched and reaches the path-rooted resolver above.
    private static bool StripInheritedBorderClaims(WorldDefinition template, WorldDefinition candidate, out WorldDefinition stripped) {
        var changed = false;
        var placements = new List<WorldPlacement>(capacity: candidate.Placements.Count);

        foreach (var placement in candidate.Placements) {
            var templatePlacement = template.Placements.FirstOrDefault(predicate: row => string.Equals(a: row.Id, b: placement.Id, comparisonType: StringComparison.Ordinal));

            if ((templatePlacement is null) || (placement.FaceSources is null)) {
                placements.Add(item: placement);

                continue;
            }

            var faces = new List<WorldPlacementFace>(capacity: placement.FaceSources.Count);

            foreach (var face in placement.FaceSources) {
                var templateFace = templatePlacement.FaceSources?.FirstOrDefault(predicate: row => string.Equals(a: row.Face, b: face.Face, comparisonType: StringComparison.Ordinal));
                var portal = face.Portal;
                var templatePortal = templateFace?.Portal;

                if ((portal?.MarginDepth is not null) && (templatePortal?.MarginDepth is not null) &&
                    string.Equals(a: portal.Destination, b: templatePortal.Destination, comparisonType: StringComparison.Ordinal) &&
                    (portal.Arrival == templatePortal.Arrival) &&
                    string.Equals(a: portal.Counterpart, b: templatePortal.Counterpart, comparisonType: StringComparison.Ordinal) &&
                    (portal.MarginDepth == templatePortal.MarginDepth)) {
                    faces.Add(item: face with { Portal = portal with { MarginDepth = null } });
                    changed = true;
                } else {
                    faces.Add(item: face);
                }
            }

            placements.Add(item: placement with { FaceSources = faces });
        }

        stripped = (changed ? candidate with { Placements = placements } : candidate);

        return changed;
    }

    // Every shipped world is a full arena, so the booted world's own template is the only base and this always
    // returns it; kept as its own method (rather than inlining `fallback` at the one call site) so a future
    // minimal template has one door to return through.
    private static WorldDefinition IdentityBase(WorldDefinition fallback) => fallback;
}
