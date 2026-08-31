using Puck.Commands;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>The owned world documents available as player identities.</summary>
/// <remarks>Every id in this catalog addresses exactly one storage location, and that rests on TWO rules, not one.
/// The character rule is carried by the type: every id here is a <see cref="WorldSafeName"/>, so
/// <see cref="WorldOwnedWorldFileName"/> escapes nothing and distinct ids map to distinct file-name strings. The
/// second rule is this catalog's own, because a file-name string is not a storage location: the directory is stored
/// on a case-insensitive filesystem, so ids are unique IGNORING CASE and every comparison here that addresses the
/// directory — the file-name match, <see cref="FindById"/>, <see cref="Create"/>'s collision guard,
/// <see cref="ReplaceFromSync"/>'s match — is <see cref="StringComparison.OrdinalIgnoreCase"/>. A file whose
/// name differs from its declared id only in case is therefore ADMITTED (it is the one file that id addresses) and
/// keeps the name it already carries: a save writes through the id's spelling, which the filesystem resolves onto
/// the existing entry without renaming it. The same case-insensitive rule is held one door earlier over an authored
/// seed list by <c>WorldDefinitionValidator.ValidatePlayerDefaults</c>.</remarks>
public sealed class WorldOwnedWorlds {
    /// <summary>The subdirectory an unadmittable document is moved into — a name outside this catalog's own
    /// <c>*.world.json</c> top-directory glob, exactly like the hand-placed <c>basis/</c> directory, so a disposed
    /// document is never enumerated again.</summary>
    public const string QuarantineDirectoryName = "unloadable";

    private static readonly WorldCellName MoveSpeedState = WorldCellName.Parse(candidate: "identity-move-speed");
    private static readonly WorldCellName TurnSpeedState = WorldCellName.Parse(candidate: "identity-turn-speed");

    private readonly string m_directory;
    private readonly List<WorldOwnedWorldDisposal> m_discarded = [];
    private readonly List<WorldIdentity> m_identities;
    private readonly List<WorldOwnedWorldRefusal> m_refused = [];
    private readonly WorldMotionDefaults m_motion;
    private readonly WorldDefinition m_template;

    private WorldDocumentSubmissionReceipt? m_lastReceipt;
    private long m_revision = 1;

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
        var paths = Directory.GetFiles(
            path: directory,
            searchOption: SearchOption.TopDirectoryOnly,
            searchPattern: $"*{WorldOwnedWorldFileName.Suffix}"
        ).Order(comparer: StringComparer.Ordinal).ToArray();
        var present = new HashSet<string>(
            collection: paths.Select(selector: Path.GetFileName)!,
            comparer: StringComparer.OrdinalIgnoreCase
        );

        var unloadable = new List<(string Path, string Reason)>();

        foreach (var path in paths) {
            if (
                !WorldDefinitionFileSource.TryLoad(
                contentHash: out _,
                definition: out var document,
                neighbours: neighbours,
                path: path,
                reason: out var reason
            ) ||
                (document?.Identity is null)
            ) {
                unloadable.Add(item: (path, (document is null)
                    ? reason
                    : $"{path} is not a valid {WorldDefinition.SchemaVersion} document: it declares no identity section, so it is not an owned world"
                ));

                continue;
            }

            // An owned world's file name IS its id through the one mapping every save and every cloud key derives from,
            // so a file whose name is not the one its declared id maps to is a document this catalog cannot address:
            // its next save would land on the OTHER name and silently replace whatever lives there. The comparison is
            // case-insensitive because the filesystem's own resolution is: a name differing from the addressed one
            // only in case IS the file that id addresses, and refusing it would wedge a catalog no save could repair.
            var fileName = Path.GetFileName(path: path);
            var addressed = WorldOwnedWorldFileName.For(id: document.Identity.Id);

            if (!string.Equals(
                a: fileName,
                b: addressed,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                RefuseInPlace(
                    fileName: fileName,
                    reason: $"declares id '{document.Identity.Id}', which this catalog stores as '{addressed}'{(present.Contains(item: addressed)
                        ? " — the name another file in this directory carries"
                        : " — a name no file in this directory carries")}; an owned world's file name is its id, so rename this file to '{addressed}' (or edit its own identity id, which no console verb reaches)"
                );

                continue;
            }

            m_identities.Add(item: new WorldIdentity(
                document: document,
                defaults: Defaults
            ));
        }

        // Disposal precedes seeding: the seed writes to the very file names being moved aside.
        DisposeUnloadable(candidates: unloadable);

        if (m_identities.Count == 0) {
            foreach (var seed in Defaults.Identities) {
                // A seed never writes over an entry that is still there — whatever put it there. Trying to save over
                // one would either destroy bytes a refusal promised to keep or turn a recoverable obstruction (a
                // directory at the deterministic catalog path) into a startup exception. The probe is the
                // filesystem's own, so it answers for the name in whatever case the entry actually carries.
                var occupied = Path.Combine(
                    path1: directory,
                    path2: WorldOwnedWorldFileName.For(id: seed.Id)
                );

                if (File.Exists(path: occupied) || Directory.Exists(path: occupied)) {
                    Console.Error.WriteLine(value: $"[identity] seed '{seed.Id}' skipped: the catalog path '{Path.GetFileName(path: occupied)}' is already occupied, and a seed never writes over an entry that is already there");

                    continue;
                }

                var identity = new WorldIdentity(
                    document: Seed(
                        motion: m_motion,
                        seed: seed,
                        template: m_template
                    ),
                    defaults: Defaults
                );

                m_identities.Add(item: identity);
                Save(identity: identity);
            }
        }
    }

    /// <summary>Gets the identities, one per owned world.</summary>
    public IReadOnlyList<WorldIdentity> All => m_identities;
    /// <summary>Gets the first owned identity used before a controller preference applies.</summary>
    /// <exception cref="InvalidOperationException">The catalog holds no identities — nothing in the directory was
    /// admitted and no seed was written.</exception>
    public WorldIdentity BootProfile => ((m_identities.Count > 0)
        ? m_identities[0]
        : throw new InvalidOperationException(message: "the owned-world catalog holds no identities — nothing in the catalog directory was admitted this boot and no seed was written; whatever was refused, discarded, or skipped is named on stderr and read back by identity.list")
    );
    /// <summary>Gets the visited world's player presentation defaults.</summary>
    public WorldPlayerDefaults Defaults { get; }
    /// <summary>Gets the documents this catalog DISPOSED OF at construction, in file-name order — the ones whose
    /// bytes are not a <c>puck.world.def.v1</c> document at all. Empty on every ordinary boot, and a one-time event
    /// otherwise, since each entry names a file moved out of the catalog directory. A document refused for a reason
    /// that can answer differently later (an unreadable file, an unresolved basis link, a validation claim resting
    /// on a neighbour) is NOT here: it is named on stderr and left where it is for the next boot.</summary>
    public IReadOnlyList<WorldOwnedWorldDisposal> Discarded => m_discarded;
    /// <summary>Gets the owned-world directory.</summary>
    public string FilePath => m_directory;
    /// <summary>Gets the documents this catalog REFUSED IN PLACE at construction, in the order they were refused —
    /// the ones still sitting in the catalog directory with their original bytes, either because the refusal can
    /// answer differently on the next boot (an unreadable file, an unresolved basis link, a validation claim resting
    /// on a neighbour) or because the file cannot be addressed as written (its name is not the one its declared id
    /// maps to). The counterpart of <see cref="Discarded"/>:
    /// nothing here was moved, and every entry is refused again on the next construction until it is repaired.</summary>
    public IReadOnlyList<WorldOwnedWorldRefusal> Refused => m_refused;
    /// <summary>Gets the latest cross-document durable-state verdict, visible to both authorities.</summary>
    public WorldDocumentSubmissionReceipt? LastReceipt => m_lastReceipt;
    /// <summary>Gets the installation id used by controller state slots.</summary>
    public Guid MachineId { get; }
    /// <summary>Observes every owner-side cross-document durable-state verdict for a tape.</summary>
    public Action<WorldDocumentSubmissionReceipt>? ReceiptTap { get; set; }
    /// <summary>Gets the local owned-world mutation counter.</summary>
    public long Revision => m_revision;

    /// <summary>One document this catalog could not admit, and where it was put.</summary>
    /// <param name="FileName">The file name it carried in the catalog directory.</param>
    /// <param name="Reason">Why it could not be admitted.</param>
    /// <param name="QuarantinePath">Where it now lives — never a path that already held a file or directory, so an
    /// earlier disposal of the same catalog name keeps its own copy and a stale directory cannot block quarantine.</param>
    /// <param name="Moved">Whether the move succeeded. A false here means the file is still in the catalog
    /// directory with its original bytes: the seeding pass skips any id whose catalog path is occupied, so nothing
    /// writes over it, and it is refused again on the next construction.</param>
    public sealed record WorldOwnedWorldDisposal(string FileName, string Reason, string QuarantinePath, bool Moved);

    /// <summary>One document this catalog refused and left exactly where it was.</summary>
    /// <param name="FileName">The file name it carries in the catalog directory.</param>
    /// <param name="Reason">Why it could not be admitted.</param>
    public sealed record WorldOwnedWorldRefusal(string FileName, string Reason);

    /// <summary>The catalog's checkpointed state — the identities as document data plus the mutation counter.
    /// Excludes <see cref="FilePath"/> (host state — the state directory a fresh instance's own construction
    /// resolves) and <see cref="LastReceipt"/> (a read-back-only diagnostic of the most recent submission, the same
    /// exclusion class as a body's <c>PressOutcome</c>/<c>StopOutcome</c> — nothing but a read-back verb consults
    /// it, and the next real submission repopulates it).</summary>
    public sealed record WorldOwnedWorldsCheckpoint(IReadOnlyList<byte[]> IdentityDocumentsJson, long Revision);

    /// <summary>Captures every identity's owned document and the mutation counter.</summary>
    public WorldOwnedWorldsCheckpoint Capture() => new(
        IdentityDocumentsJson: [.. m_identities.Select(selector: identity => WorldDefinitionSerialization.Serialize(definition: identity.Document!))],
        Revision: m_revision
    );
    /// <summary>Restores every identity from a previously captured checkpoint. The identity list is replaced
    /// wholesale — this never merges onto whatever the directory load already seeded.</summary>
    public void Restore(WorldOwnedWorldsCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        m_identities.Clear();

        foreach (var json in checkpoint.IdentityDocumentsJson) {
            m_identities.Add(item: new WorldIdentity(
                defaults: Defaults,
                document: WorldDefinitionSerialization.Deserialize(utf8Json: json)
            ));
        }

        m_revision = checkpoint.Revision;
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
            return Refuse(
                reason: "source document id is missing",
                submission: submission
            );
        }
        if (
            (FindById(id: submission.OwnerDocumentId) is not { } owner) ||
            (owner.Document is not { } document)
        ) {
            return Refuse(
                reason: $"owner world '{submission.OwnerDocumentId}' is unavailable",
                submission: submission
            );
        }
        if (string.IsNullOrWhiteSpace(value: submission.Slot)) {
            return Refuse(
                reason: "slot name is missing",
                submission: submission
            );
        }

        var principal = WorldPrincipal.Document(id: submission.SourceDocumentId);
        var subject = GrantSubject.State(name: submission.Slot);
        var grant = document.Grants.FirstOrDefault(predicate: candidate =>
            ((candidate.Principal == principal) &&
            (candidate.Capability == WorldCapability.Mutate) &&
            (candidate.Subject == subject)));
        // The WRITE mask, never the kind mask: this door's vocabulary is WorldDocumentWriteKind (replace vs.
        // accumulate), and the two are distinct types precisely so this call site cannot read a mutation-kind lane
        // as an operation lane. A row with no write mask reaches nothing here — this channel's mask is REQUIRED
        // (unlike an Edit row's optional narrowing), because a foreign document's write is deny-by-default and the
        // mask is the whole of what admits it.
        if (
            (grant.Principal != principal) ||
            (grant.WriteMask is not { } writes) ||
            !writes.Contains(kind: submission.Kind)
        ) {
            return Refuse(
                submission: submission,
                reason: $"{principal.Describe()} has no {submission.Kind.ToString().ToLowerInvariant()} grant for {subject.Describe()}"
            );
        }
        if (
            !owner.TryReadState(
            name: submission.Slot,
            row: out var row
        ) ||
            (row is null)
        ) {
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
            return Refuse(
                submission: submission,
                reason: $"unknown slot '{submission.Slot}' — {subject.Describe()} must be declared in the recipient's OWN document before an external document can write into it; there is no spooled delivery for an undeclared row"
            );
        }

        // TEXT branch — a SEPARATE arm off the SAME door, never a sibling door: the submitter varies (sim-driven
        // numeric outputs below vs. a player-initiated text delivery here), but the admission predicate above
        // (grant + write mask) already ran identically for both. Checked ahead of the numeric switch because
        // row.Kind == CellKind.Text can never match either numeric arm, so routing it here first is strictly a
        // clearer refusal, not a different gate.
        if (submission.Text is { } text) {
            if (row.Kind != CellKind.Text) {
                return Refuse(
                    submission: submission,
                    reason: $"{subject.Describe()} has the wrong storage kind"
                );
            }
            // TEXT IS SET-ONLY: Add would mean "concatenate", which this door never does — silently accumulating
            // strings is concatenation-by-stealth. Checked here (not folded into the write-mask gate above) because
            // a grant CAN legitimately admit writes:Set,Add (e.g. shared with a numeric row) — the row's own shape
            // is what refuses Add for text, not the grant.
            if (submission.Kind == WorldDocumentWriteKind.Add) {
                return Refuse(
                    submission: submission,
                    reason: $"{subject.Describe()} is text — Add refuses by name (text is Set-only; no concatenation-by-stealth)"
                );
            }
            if (text.Length > WorldStateCapacity.MaxTextValueLength) {
                return Refuse(
                    submission: submission,
                    reason: $"text is {text.Length} UTF-16 code units, past {subject.Describe()}'s {WorldStateCapacity.MaxTextValueLength}-unit cap"
                );
            }

            // TWO admitted row shapes: a SLOT overwrites (the original, single-value durable-slot delivery); a
            // BOUNDED, EVICTING keyed row (WorldStateRow.Evicts + Capacity — e.g. a chat inbox) APPENDS instead,
            // through the SAME primitive a self-authored chat log uses (WorldIdentity.TryAppendEvictingText), so a
            // whisper landing in a bounded inbox and a player appending their own log can never disagree about
            // eviction order or key uniqueness. The appended cell's key is minted from the RECIPIENT's own document
            // (never wire-supplied), so a foreign document can never choose or collide a key.
            if (row.IsSlot) {
                owner.WriteState(row: row with {
                    Cells = [new WorldStateCell(
                        Key: WorldStateRow.SlotKey,
                        Text: text
                    )],
                });
            } else if (
                row.Evicts &&
                (row.Capacity is not null)
            ) {
                if (!owner.TryAppendEvictingText(
                    rowName: row.Name,
                    text: text,
                    evictedKey: out _,
                    reason: out var appendReason
                )) {
                    return Refuse(
                        submission: submission,
                        reason: $"{subject.Describe()} {appendReason}"
                    );
                }
            } else {
                return Refuse(
                    submission: submission,
                    reason: $"{subject.Describe()} has the wrong storage kind — a text delivery lands only on a slot or a bounded, evicting row"
                );
            }

            Save(identity: owner);
            return new WorldDocumentSubmissionReceipt(
                Accepted: true,
                Reason: "owner accepted the granted operation",
                Submission: submission
            );
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
            ( { Kind: CellKind.Fixed, IsSlot: true }, ActionStateKind.Counter) => true,
            ( { Kind: CellKind.Int, IsSlot: true }, ActionStateKind.Timer) => true,
            _ => false,
        };

        if (!storageMatches) {
            return Refuse(
                submission: submission,
                reason: $"{subject.Describe()} has the wrong storage kind"
            );
        }

        try {
            var current = row.Cells![0].Value;
            var value = ((submission.Kind == WorldDocumentWriteKind.Add)
                ? checked((current + submission.Value))
                : submission.Value
            );

            if (
                row.NonNegative &&
                (value < 0)
            ) {
                return Refuse(
                    submission: submission,
                    reason: $"value {value} is negative — {subject.Describe()}'s floor is non-negative"
                );
            }

            if (
                ((row.Min is { } minimum) && (value < minimum)) ||
                ((row.Max is { } maximum) && (value > maximum))
            ) {
                return Refuse(
                    submission: submission,
                    reason: $"value {value} is outside {subject.Describe()}'s authored envelope"
                );
            }

            owner.WriteState(row: row with {
                Cells = [new WorldStateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: value
                )],
            });
        } catch (OverflowException) {
            return Refuse(
                submission: submission,
                reason: $"{subject.Describe()} overflowed"
            );
        }

        Save(identity: owner);
        return new WorldDocumentSubmissionReceipt(
            Accepted: true,
            Reason: "owner accepted the granted operation",
            Submission: submission
        );
    }
    // Disposal is decided by the CLASS of the loader's refusal, never by the mere fact of one. A document-shape
    // refusal is a verdict on the bytes, so the file leaves the *.world.json glob once, is named once rather than on
    // every construction, and the catalog it empties is what the seeding block fills; nothing here can tell a
    // deliberately retired shape from a corrupt file, so neither is silently eaten and neither is migrated. Every
    // other refusal (see IsTerminalDocumentShape) can answer differently on the next boot, so its file STAYS and is
    // only named. Both narrations group by reason, so one fault across a whole directory reads as one group while a
    // lone file stands in a group of its own.
    private void DisposeUnloadable(IReadOnlyList<(string Path, string Reason)> candidates) {
        if (candidates.Count == 0) {
            return;
        }
        var quarantine = Path.Combine(
            path1: m_directory,
            path2: QuarantineDirectoryName
        );
        var retained = new List<(string FileName, string Reason)>();

        foreach (var (path, reason) in candidates) {
            var detail = Strip(
                path: path,
                reason: reason
            );

            if (!IsTerminalDocumentShape(
                path: path,
                reason: reason
            )) {
                var fileName = Path.GetFileName(path: path);

                retained.Add(item: (fileName, detail));
                m_refused.Add(item: new WorldOwnedWorldRefusal(
                    FileName: fileName,
                    Reason: detail
                ));

                continue;
            }

            var destination = QuarantineDestination(
                fileName: Path.GetFileName(path: path),
                quarantine: quarantine
            );
            var moved = false;

            try {
                _ = Directory.CreateDirectory(path: quarantine);
                File.Move(
                    destFileName: destination,
                    overwrite: false,
                    sourceFileName: path
                );

                moved = true;
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                detail = $"{detail} — and it could not be moved aside ({exception.Message.ReplaceLineEndings(replacementText: " ")}), so its bytes stay where they are and it will be named again on the next boot";
            }

            m_discarded.Add(item: new WorldOwnedWorldDisposal(
                FileName: Path.GetFileName(path: path),
                Moved: moved,
                QuarantinePath: destination,
                Reason: detail
            ));
        }

        if (retained.Count > 0) {
            Console.Error.WriteLine(value: $"[identity] refused {retained.Count} owned world(s) this boot could not read, left where they are for the next one: {Narrate(
                entries: retained
            )}");
        }
        if (m_discarded.Count > 0) {
            Console.Error.WriteLine(value: $"[identity] discarded {m_discarded.Count} unloadable owned world(s) into '{quarantine}' — a document shape this catalog no longer reads is disposed of, never migrated: {Narrate(
                entries: [.. m_discarded.Select(selector: entry => (entry.FileName, entry.Reason))]
            )}");
        }
    }
    // Every shipped world is a full arena, so the booted world's own template is the only base and this always
    // returns it; kept as its own method (rather than inlining `fallback` at the one call site) so a future
    // minimal template has one door to return through.
    private static WorldDefinition IdentityBase(WorldDefinition fallback) => fallback;
    // The one predicate that decides whether a refusal is a verdict on the file's BYTES or on the moment it was read
    // in — the loader's own reason classes, matched on the wording WorldDefinitionFileSource.TryLoad documents.
    // Quarantine is irreversible from the catalog's side (the name it frees is re-seeded), so only the byte verdict
    // earns it: a document that does not parse as puck.world.def.v1 parses no better on the next boot.
    //
    // The rest each answer differently on a later call and would CASCADE if quarantined. "cannot read" is a lock or
    // a half-written file; "no file at" is a file that vanished between the enumeration and the load; "basis
    // composition refused" is a link not placed yet; "document validation refused" may rest on an adjacency
    // neighbour resolved against this very directory — the one the sweep is emptying — so one refusal would take its
    // neighbours down with it on the following boot, and the seeding pass would write defaults over every freed name.
    private static bool IsTerminalDocumentShape(string path, string reason) => (
        reason.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: $"{path} is not a valid {WorldDefinition.SchemaVersion} document:"
        ) ||
        reason.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: $"cannot decode {path}:"
        )
    );
    private static string Narrate(IReadOnlyList<(string FileName, string Reason)> entries) => string.Join(
        separator: "; ",
        values: entries
            .GroupBy(
                comparer: StringComparer.Ordinal,
                keySelector: entry => entry.Reason
            )
            .Select(selector: group => $"{string.Join(
                separator: ", ",
                values: group.Select(selector: entry => entry.FileName)
            )} — {group.Key}")
    );
    // A quarantine name is DERIVED from the catalog name, and the catalog re-seeds the name a disposal frees, so the
    // same name reaches this directory again carrying different bytes. Quarantine exists to keep those bytes
    // readable, so any occupied entry (file or directory) takes the next free ordinal suffix rather than blocking or
    // overwriting the earlier copy. The suffixed name still sits outside the catalog's own top-directory glob, like
    // every other file here.
    private static string QuarantineDestination(string quarantine, string fileName) {
        var candidate = Path.Combine(
            path1: quarantine,
            path2: fileName
        );

        for (var ordinal = 2; (File.Exists(path: candidate) || Directory.Exists(path: candidate)); ordinal++) {
            candidate = Path.Combine(
                path1: quarantine,
                path2: $"{fileName}.{ordinal}"
            );
        }

        return candidate;
    }
    private static WorldDocumentSubmissionReceipt Refuse(WorldDocumentSubmission submission, string reason) => new(
        Accepted: false,
        Reason: reason,
        Submission: submission
    );
    // The one door for "this document parses, and stays exactly where it is": names it on stderr AND records it in
    // Refused, so a session that starts after the boot line scrolls away can still learn the file exists.
    private void RefuseInPlace(string fileName, string reason) {
        m_refused.Add(item: new WorldOwnedWorldRefusal(
            FileName: fileName,
            Reason: reason
        ));
        Console.Error.WriteLine(value: $"[identity] owned world refused: '{fileName}' {reason}");
    }
    private static WorldDefinition Seed(WorldDefinition template, WorldMotionDefaults motion, WorldIdentitySeed seed) => template with {
        DocumentId = seed.Id,
        MotionRaw = motion,
        Identity = new WorldIdentityDefinition(
        Id: seed.Id,
        Name: seed.Name,
        Color: seed.Color,
        MoveSpeedState: MoveSpeedState,
        TurnSpeedState: TurnSpeedState
    ),
        StateRaw = ((template.StateRaw ?? new WorldStateSection()) with {
            // The identity rows FOLD OVER the template's authored world rows rather than replacing the list — the
            // template's views/camera programs may reference its own state rows (e.g. a seatRig yaw reading a
            // state.<row>.<key> cell), and a seeded document that drops them refuses validation on its next load.
            World = SeedWorldState(
            templateRows: template.StateRaw?.World,
            motion: motion
        ),
        }),
        BindingOverlaysRaw = [],
        // The template's own hud policy (enabled/cursor) survives; only its authored panels are stripped — an owned
        // world starts panel-clean but keeps the document-authored cursor, never an engine value.
        HudRaw = new WorldHudSection(
            Defaults: template.Hud.Defaults,
            Panels: []
        ),
        Adjacencies = null,
    };
    // The seeded world-state rows: the template's authored rows with the two identity speed slots folded over any
    // same-named rows.
    private static IReadOnlyList<WorldStateRow> SeedWorldState(IReadOnlyList<WorldStateRow>? templateRows, WorldMotionDefaults motion) {
        var rows = new List<WorldStateRow>(capacity: ((templateRows?.Count ?? 0) + 2));

        if (templateRows is not null) {
            foreach (var row in templateRows) {
                if ((row.Name != MoveSpeedState) && (row.Name != TurnSpeedState)) {
                    rows.Add(item: row);
                }
            }
        }

        rows.Add(item: new WorldStateRow(
            Name: MoveSpeedState,
            Kind: CellKind.Fixed,
            Cells: [new WorldStateCell(
                Key: WorldStateRow.SlotKey,
                Value: Puck.Maths.FixedQ4816.FromDouble(value: motion.MoveSpeed).Value
            )]
        ));
        rows.Add(item: new WorldStateRow(
            Name: TurnSpeedState,
            Kind: CellKind.Fixed,
            Cells: [new WorldStateCell(
                Key: WorldStateRow.SlotKey,
                Value: Puck.Maths.FixedQ4816.FromDouble(value: motion.TurnSpeed).Value
            )]
        ));

        return rows;
    }

    // The result is a GROUPING KEY as well as a narration, so it must carry nothing that varies per file: the loader
    // spells the path at the head of some reasons ("{path} is not a valid …") and mid-sentence in others ("no file
    // at {path}", "cannot read {path}: …"), and the operating system's own message quotes it again. Every occurrence
    // becomes one file-independent placeholder so two files failing the same way share a key, a leading placeholder
    // then drops because the file name is already carried beside the reason, and no absolute path — the player's
    // state directory — reaches the console.
    private static string Strip(string path, string reason) {
        const string Placeholder = "the file";

        var text = (reason ?? string.Empty).Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: Placeholder,
            oldValue: path
        ).Trim();

        return (text.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Placeholder
        )
            ? text[Placeholder.Length..].TrimStart()
            : text
        );
    }

    /// <summary>Creates and persists one owned world. <paramref name="name"/> is a <see cref="WorldSafeName"/>, so
    /// what is left to refuse here is a collision, in either of the two places one can live: an id or display name
    /// this catalog already holds (<c>FindById</c>/<c>Find</c>, both ignoring case), or an entry occupying the id's
    /// catalog path that this boot did not admit — a refused document, a failed disposal, or a directory. The second
    /// check reads the directory rather than the identity list, because a boot that admitted nothing leaves the list
    /// empty while the bytes are still on disk, and <see cref="Save(WorldIdentity)"/> would write straight over
    /// them.</summary>
    /// <param name="name">The new world's id and display name.</param>
    /// <param name="colorHex">The avatar color.</param>
    /// <param name="reason">Why creation was refused, or empty on success.</param>
    /// <returns>The created identity, or <see langword="null"/> with <paramref name="reason"/> set.</returns>
    public WorldIdentity? Create(WorldSafeName name, string colorHex, out string reason) {
        if (
            (Find(name: name) is not null) ||
            (FindById(id: name) is not null)
        ) {
            reason = $"'{name}' already exists";
            return null;
        }

        var occupied = Path.Combine(
            path1: m_directory,
            path2: WorldOwnedWorldFileName.For(id: name)
        );

        if (File.Exists(path: occupied) || Directory.Exists(path: occupied)) {
            reason = $"the catalog path '{Path.GetFileName(path: occupied)}' is already occupied by an entry this boot did not admit — saving there would write over it; repair or remove it (identity.list's refused=/discarded= columns and the boot's stderr lines name it)";
            return null;
        }
        var identity = new WorldIdentity(
            document: Seed(
                template: m_template,
                motion: m_motion,
                seed: new WorldIdentitySeed(
                    Color: colorHex,
                    Id: name,
                    Name: name
                )
            ),
            defaults: Defaults
        );

        m_identities.Add(item: identity);
        Save(identity: identity);
        reason = string.Empty;
        return identity;
    }
    /// <summary>Finds an identity by display name.</summary>
    public WorldIdentity? Find(string name) => m_identities.FirstOrDefault(predicate: identity => string.Equals(
        a: identity.Name,
        b: name,
        comparisonType: StringComparison.OrdinalIgnoreCase
    ));
    /// <summary>Finds an identity by owned-world id, ignoring case — two spellings differing only in case name one
    /// storage location, so they name one identity.</summary>
    public WorldIdentity? FindById(string id) => m_identities.FirstOrDefault(predicate: identity => string.Equals(
        a: identity.Id,
        b: id,
        comparisonType: StringComparison.OrdinalIgnoreCase
    ));
    /// <summary>Resolves a reconnect-stable controller from state-slot references in the owned worlds.</summary>
    public WorldIdentity? PreferredProfile(InputDeviceId device) {
        if (
            (device.Persistence != InputDeviceIdentityPersistence.Reconnect) ||
            (MachineId == Guid.Empty)
        ) {
            return null;
        }
        WorldIdentity? match = null;

        foreach (var identity in m_identities) {
            foreach (var slots in (identity.Document?.Identity?.Controllers ?? [])) {
                if (
                    identity.TryReadState(
                    name: slots.MachineState,
                    row: out var machineRow
                ) &&
                    ((machineRow is { Kind: CellKind.Text, IsSlot: true }) && (machineRow.Cells![0].Text is { } machine)) &&
                    identity.TryReadState(
                    name: slots.DeviceState,
                    row: out var deviceRow
                ) &&
                    ((deviceRow is { Kind: CellKind.Text, IsSlot: true }) && (deviceRow.Cells![0].Text is { } storedDevice)) &&
                    string.Equals(
                    a: machine,
                    b: MachineId.ToString(format: "D"),
                    comparisonType: StringComparison.OrdinalIgnoreCase
                ) &&
                    string.Equals(
                    a: storedDevice,
                    b: device.Value.ToString(format: "D"),
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )
                ) {
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
        if (
            (device.Persistence != InputDeviceIdentityPersistence.Reconnect) ||
            (MachineId == Guid.Empty) ||
            (profile.Document?.Identity is not { } definition)
        ) {
            return;
        }
        var key = device.Value.ToString(format: "N");
        var slots = new WorldControllerStateSlots(
            MachineState: WorldCellName.Parse(candidate: $"controller-{key}-machine"),
            DeviceState: WorldCellName.Parse(candidate: $"controller-{key}-device")
        );

        profile.WriteState(row: new WorldStateRow(
            Name: slots.MachineState,
            Kind: CellKind.Text,
            Cells: [new WorldStateCell(
                    Key: WorldStateRow.SlotKey,
                    Text: MachineId.ToString(format: "D")
                )]
        ));
        profile.WriteState(row: new WorldStateRow(
            Name: slots.DeviceState,
            Kind: CellKind.Text,
            Cells: [new WorldStateCell(
                    Key: WorldStateRow.SlotKey,
                    Text: device.Value.ToString(format: "D")
                )]
        ));
        profile.ReplaceDocument(document: profile.Document with { Identity = definition with { Controllers = [.. (definition.Controllers ?? []), slots] } });
        Save(identity: profile);
    }
    /// <summary>Adopts a pulled cloud copy of an owned world: replaces the in-memory identity that shares its id (or
    /// adds a new one), then persists it locally through the ordinary save path. The caller has already validated the
    /// document through the boot loader's gate — which means its id already survived <see cref="WorldSafeName"/>'s
    /// JSON parse — so what is left to refuse here is a document without an identity section at all, and a document
    /// whose id collides with a local id in case only. The cloud namespace is case-SENSITIVE and this catalog's
    /// directory is not, so <c>Amber</c> and <c>amber</c> are two blobs that would adopt onto one local file:
    /// refusing by name is what keeps a pull from replacing a world it never named.</summary>
    /// <param name="document">The validated world document to adopt.</param>
    /// <param name="reason">What happened — replaced, added, or why it was refused.</param>
    /// <returns>Whether the document was adopted.</returns>
    public bool ReplaceFromSync(WorldDefinition document, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: document);
        if (document.Identity is null) {
            reason = "document has no identity section";
            return false;
        }
        var incoming = new WorldIdentity(
            document: document,
            defaults: Defaults
        );
        var index = m_identities.FindIndex(match: candidate => string.Equals(
            a: candidate.Id,
            b: incoming.Id,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ));

        if (index >= 0) {
            if (!string.Equals(
                a: m_identities[index].Id,
                b: incoming.Id,
                comparisonType: StringComparison.Ordinal
            )) {
                reason = $"id '{incoming.Id}' differs from the owned world '{m_identities[index].Id}' in case only, and both address one local file — push it to the local spelling's own key, or rename its identity to '{m_identities[index].Id}'";
                return false;
            }
            m_identities[index] = incoming;
            reason = "replaced the local copy";
        } else {
            m_identities.Add(item: incoming);
            reason = "added a new owned world";
        }
        Save(identity: incoming);
        return true;
    }
    /// <summary>Persists one identity, preserving the derivation of the file it overwrites — the same
    /// <see cref="WorldDefinitionSerialization.SavePreservingBasis"/> contract <c>world.save</c> runs for the live
    /// world's own document, applied to an owned world's own catalog file. An identity whose file authors a
    /// <c>basis</c> (a hand-placed delta in <see cref="FilePath"/>'s <c>basis/</c> subdirectory, outside this
    /// catalog's own <c>*.world.json</c> glob) keeps that derivation across every save; a flat identity keeps
    /// writing flat. <see cref="Revision"/> advances only when the write actually changed the file's bytes, so a
    /// caller that re-saves an identity already matching disk (e.g. a push publishing live state before resolving
    /// its chain) never dirties the catalog by itself.</summary>
    /// <param name="identity">The identity to persist.</param>
    public void Save(WorldIdentity identity) {
        if (
            (identity.Document is not { } document) ||
            (document.Identity is not { } identitySection)
        ) {
            return;
        }
        var path = Path.Combine(
            path1: m_directory,
            path2: WorldOwnedWorldFileName.For(id: identitySection.Id)
        );
        // A change detector, never a correctness input: the write below happens either way, and a file this process
        // cannot read right now is indistinguishable from one whose bytes are about to differ, so an unreadable
        // "before" takes the same arm an absent one takes and Revision advances. Throwing here would throw out of
        // the constructor, since seeding saves.
        byte[]? before;

        try {
            before = (File.Exists(path: path)
                ? File.ReadAllBytes(path: path)
                : null
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            before = null;
        }

        _ = WorldDefinitionSerialization.SavePreservingBasis(
            basisPath: out _,
            definition: document,
            note: out var note,
            path: path
        );

        if (note.Length > 0) {
            Console.Error.WriteLine(value: $"[identity] {note}");
        }
        if (
            (before is null) ||
            !before.AsSpan().SequenceEqual(other: File.ReadAllBytes(path: path))
        ) {
            m_revision++;
        }
    }
    /// <summary>Persists every owned world.</summary>
    public void Save() {
        foreach (var identity in m_identities) {
            Save(identity: identity);
        }
    }
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
        if (
            (FindById(id: ownerId) is not { } owner) ||
            (owner.Document is not { } document)
        ) {
            reason = $"owner world '{ownerId}' is unavailable";
            return false;
        }
        var principal = WorldPrincipal.Document(id: sourceDocumentId);
        var subject = GrantSubject.State(name: slot);

        if (!document.Grants.Any(predicate: grant => ((grant.Principal == principal) && (grant.Capability == WorldCapability.Observe) && (grant.Subject == subject)))) {
            reason = $"{principal.Describe()} has no read grant for {subject.Describe()}";
            return false;
        }
        if (!owner.TryReadState(
            name: slot,
            row: out var row
        )) {
            reason = $"unknown slot '{slot}'";
            return false;
        }
        switch (row, kind) {
            case ( { Kind: CellKind.Fixed, IsSlot: true } fixedRow, ActionStateKind.Counter):
                value = new DurableStateValue(
                    Name: slot,
                    Value: Puck.Maths.FixedQ4816.FromRawBits(value: fixedRow.Cells![0].Value),
                    TimerTicks: 0
                );
                reason = string.Empty;
                return true;
            // A tick count crosses as an unsigned quantity, so a negative cell cannot be read as one. That is a
            // DIFFERENT refusal from a kind mismatch and says so: the row a caller named is the right kind and holds a
            // value this lane cannot represent. It is reachable only on a row that does NOT declare
            // WorldStateRow.NonNegative — declaring it is what makes an Int row a timer, and the write door and the
            // document validator both hold that floor.
            case ( { Kind: CellKind.Int, IsSlot: true } intRow, ActionStateKind.Timer):
                if (intRow.Cells![0].Value < 0) {
                    reason = $"{subject.Describe()} holds {intRow.Cells![0].Value}, which no tick count can carry — an int row read as a timer must declare a non-negative floor";
                    return false;
                }
                value = new DurableStateValue(
                    Name: slot,
                    Value: Puck.Maths.FixedQ4816.Zero,
                    TimerTicks: checked((ulong)intRow.Cells![0].Value)
                );
                reason = string.Empty;
                return true;
            default:
                reason = $"{subject.Describe()} has the wrong storage kind";
                return false;
        }
    }
}
