using Puck.Storage;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.World.Server;

/// <summary>One owned world's sync verdict — the id, whether the operation landed, and the human-readable detail the
/// verb echoes.</summary>
/// <param name="Id">The owned world id.</param>
/// <param name="Ok">Whether the operation landed.</param>
/// <param name="Detail">What happened — the receipt, or why it was refused.</param>
public readonly record struct WorldSyncOutcome(string Id, bool Ok, string Detail);
/// <summary>What the most recent push actually did — the honest three-valued answer <c>storage.status</c> echoes,
/// distinguishing "no write has been attempted or every one landed" from the two ways a write fails, because a status
/// line that only tracked the precondition bit read <c>ok</c> after a run in which every push was refused.</summary>
public enum WorldSyncWriteOutcome {
    /// <summary>No push has been attempted this session, or every world in the last one landed.</summary>
    Ok,
    /// <summary>At least one world hit an if-match precondition — the cloud copy moved since last sync.</summary>
    PreconditionFailed,
    /// <summary>At least one world failed for another reason — a transport error, a timeout, an unsynced cloud copy,
    /// or a key its id cannot address.</summary>
    Failed,
}
/// <summary>
/// The owned-world cloud sync engine: pushes and pulls whole world documents against the per-user container, one blob
/// per world under <c>puck/worlds/</c>, carrying the storage version token so a stale writer is refused rather than
/// clobbering a newer copy (refuse-and-surface; a refusal names the remedy). First contact is fail-closed: a push with
/// no tracked token uses create-only, so a cloud copy this catalog has never seen is never overwritten. Tokens persist
/// in a sidecar beside the owned worlds because they are storage facts; the last-synced cursor is session-local — the
/// catalog revision it compares against is a process counter, so a persisted cursor would compare two unrelated
/// boots. A fresh session therefore reports dirty until its first fully successful whole-catalog push or pull, which
/// errs on the side that prompts a sync rather than the side that fakes one. Per-world detail lines are the truth;
/// the cursor is the catalog-level approximation.
/// <para>One blob name per world id means the id must name the blob unambiguously, and
/// <see cref="WorldOwnedWorldFileName"/> is lossy (every reserved character collapses to <c>'_'</c>), so an id
/// that does not survive it — or that escapes onto a name another catalog id already claims — is refused by name at
/// both push and pull rather than quietly sharing a stranger's key. A whole-catalog <see cref="Pull"/> also discovers
/// cloud-only worlds by listing the <c>puck/worlds/</c> namespace and inverting that same mapping; a listed name the
/// mapping could never have emitted belongs to no reachable id and is refused by name too, so an operator learns the
/// object exists instead of watching it vanish.</para>
/// <para>The key an operation addresses is chosen from the requested id, so a pull also refuses a cloud document whose
/// own identity <c>id</c> is not that id: adopting it would key the document under one name and the version token
/// under another, overwriting whichever local world the document happens to name and leaving the adopted copy
/// unpushable. Adoption keys the document under its own identity <c>id</c> and runs only <see cref="WorldOwnedWorlds.ReplaceFromSync"/>'s
/// save-side rule — it replaces the same-id entry or adds a new one, refusing merely a document with no identity
/// section, and does not apply <see cref="WorldOwnedWorlds.Create"/>'s display-name-collision check.</para>
/// <para>Operations block the console pump — and with it the frame loop it drains on — for up to 15 seconds PER
/// BLOB touched, never once per call: a <see cref="Push"/> or <see cref="Pull"/> of an N-link chain touches N blobs,
/// each under its own 15-second budget, and <see cref="WorldStorageNeighbourResolver.Resolve"/> reads a root (15s)
/// then composes its chain (a second, independent 15s budget) — up to 30 seconds for one neighbour. Transport
/// errors surface as refusals, never silently.</para>
/// <para>An owned world naming a <c>basis</c> (a hand-placed delta over a sibling in its catalog directory's
/// <c>basis/</c> subdirectory — outside the catalog's own directory glob, so it never enumerates as a second owned
/// world) pushes and pulls its WHOLE chain, not just its flattened tip: <see cref="Push"/> pushes each chain link
/// under its own <c>puck/worlds/basis/{name}</c> key (<see cref="BasisAddressFor"/>), deduplicated within one
/// <see cref="Push"/> call when two owned worlds share a link, with a per-link outcome named <c>"{name} (basis)"</c>
/// alongside the owned world's own; <see cref="Pull"/> composes the chain before the boot gate ever sees it, over
/// <see cref="WorldStorageDocumentSource"/> — the storage twin of the directory walk
/// <see cref="WorldDefinitionFileSource"/> runs for a local load.</para>
/// </summary>
public sealed class WorldOwnedWorldSync {
    // The one spelling of the basis-chain namespace segment — BasisAddressFor and DiscoverCloudIds' skip check both
    // read this constant rather than each re-spelling "puck/worlds/basis".
    internal const string BasisNamespace = (WorldsNamespace + "/basis");
    // Engine-owned data sits under a puck/ root so the per-user container stays shared with the platform's own
    // namespaces (private/keys, private/message.txt) rather than colonizing the container root. Internal (not
    // private): WorldStorageNeighbourResolver addresses a neighbour's blob under this SAME namespace, and quoting
    // one constant is how the two never drift apart.
    internal const string WorldsNamespace = "puck/worlds";

    // Bounds a discovery transport exception's message to one flat console line — see DiscoverCloudIds' catch.
    private const int DiscoveryDetailLengthLimit = 200;

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(seconds: 15);

    private readonly Guid m_containerId;
    private readonly ICounterpartPublisher? m_publisher;
    private readonly string m_stateFilePath;
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;
    private readonly WorldOwnedWorlds m_worlds;

    private long m_lastSyncedRevision;

    private string m_lastClaimDetail = "no counterpart claim posted this session";

    private WorldSyncWriteOutcome m_lastWrite;

    private readonly Dictionary<string, string> m_basisTokens = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, string> m_tokens = new(comparer: StringComparer.Ordinal);

    /// <summary>Initializes the engine and loads the sidecar state.</summary>
    /// <param name="worlds">The owned-world catalog.</param>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The storage target (the per-user cloud endpoint).</param>
    /// <param name="containerId">The per-user container id the identity resolver produced.</param>
    /// <param name="stateFilePath">The sidecar file the tokens and cursor persist in.</param>
    /// <param name="publisher">Publishes this world's own counterpart claim after each successful push;
    /// <see langword="null"/> leaves that half of a push a no-op (the document write is unaffected either way).</param>
    /// <exception cref="ArgumentNullException"><paramref name="worlds"/>, <paramref name="store"/>, or <paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stateFilePath"/> is <see langword="null"/> or whitespace.</exception>
    public WorldOwnedWorldSync(WorldOwnedWorlds worlds, IObjectBlobStore store, ObjectStorageTarget target, Guid containerId, string stateFilePath, ICounterpartPublisher? publisher = null) {
        ArgumentNullException.ThrowIfNull(argument: worlds);
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: stateFilePath);
        m_containerId = containerId;
        m_publisher = publisher;
        m_stateFilePath = stateFilePath;
        m_store = store;
        m_target = target;
        m_worlds = worlds;
        LoadState();
    }

    /// <summary>Gets a value indicating whether the catalog has moved past the last fully synced revision this session.</summary>
    public bool Dirty => (m_worlds.Revision != m_lastSyncedRevision);
    /// <summary>Gets what the most recently pushed world's counterpart claim post did — the addressable-later half
    /// of a push, echoed by <c>storage.status</c> alongside <see cref="LastWrite"/>.</summary>
    public string LastClaimDetail => m_lastClaimDetail;
    /// <summary>Gets the catalog revision at the last fully successful whole-catalog push or pull THIS SESSION; 0 before
    /// one happens, so a fresh boot reads as unsynced (the safe side).</summary>
    public long LastSyncedRevision => m_lastSyncedRevision;
    /// <summary>Gets what the most recent push actually did — <see cref="WorldSyncWriteOutcome.Ok"/> before any push and
    /// after one where every world landed, else the way it failed.</summary>
    public WorldSyncWriteOutcome LastWrite => m_lastWrite;
    /// <summary>Gets how many owned worlds carry a tracked cloud token.</summary>
    public int TrackedCount => m_tokens.Count;

    private static string Abbreviate(string? token) {
        var bare = (token ?? string.Empty).Trim(trimChar: '"');

        return ((bare.Length == 0)
            ? "none"
            : ((bare.Length <= 12)
                ? bare
                : bare[..12]
        ));
    }
    /// <summary>Lists the cloud <c>puck/worlds/</c> namespace and adds any id this catalog does not already know about to
    /// <paramref name="ids"/>. An id is only recoverable when re-escaping the candidate extracted from a blob name
    /// reproduces that exact name through <see cref="WorldOwnedWorldFileName.For"/>. A name that does not is one
    /// nothing here could have written — this engine refuses to push such an id in the first place — so it was placed
    /// by something that does not share this mapping, and no owned-world id addresses it. It is surfaced as a named
    /// refusal in <paramref name="refusals"/> rather than silently skipped, because "you have a cloud object this
    /// engine cannot reach" is exactly the fact an operator needs.</summary>
    private void DiscoverCloudIds(SortedSet<string> ids, List<WorldSyncOutcome> refusals) {
        IReadOnlyList<string> keys;

        try {
            using var timeout = new CancellationTokenSource(delay: OperationTimeout);

            keys = m_store.ListAsync(
                cancellationToken: timeout.Token,
                keyPrefix: $"{WorldsNamespace}/",
                objectId: m_containerId,
                target: m_target
            ).AsTask().GetAwaiter().GetResult();
        } catch (OperationCanceledException) {
            refusals.Add(item: new WorldSyncOutcome(
                Id: "*",
                Ok: false,
                Detail: $"cloud discovery timed out after {OperationTimeout.TotalSeconds:0}s"
            ));
            return;
        } catch (InvalidOperationException exception) {
            // NOT a transport failure — the store declined to send the request at all (an edge-shaped target with no
            // DirectEndpoint authored; see AzureBlobObjectBlobStoreBackend.GetListServiceClient). Calling that a
            // transport error would send an operator to the network for a configuration answer, so it gets its own
            // word. Uncapped on purpose: this message is authored, and its TAIL is the remedy.
            refusals.Add(item: new WorldSyncOutcome(
                Id: "*",
                Ok: false,
                Detail: $"cloud discovery refused — {exception.Message.ReplaceLineEndings(replacementText: " ")}"
            ));
            return;
        } catch (Exception exception) {
            // A LIST call is the one this class makes with no guarantee its failure text is a single-line Storage
            // error: a genuine transport failure against a direct endpoint can carry an arbitrarily long, multi-line
            // body with nothing actionable at its end. Flatten and cap it so one console line stays one line.
            refusals.Add(item: new WorldSyncOutcome(
                Id: "*",
                Ok: false,
                Detail: $"cloud discovery transport error — {FlattenDetail(message: exception.Message)}"
            ));
            return;
        }

        foreach (var key in keys) {
            if (key.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: $"{BasisNamespace}/"
            )) {
                continue; // basis-chain links live in their own namespace segment; never a pullable owned world.
            }

            var slash = key.LastIndexOf(value: '/');
            var fileName = ((slash >= 0)
                ? key[(slash + 1)..]
                : key
            );

            if (!fileName.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldOwnedWorldFileName.Suffix
            )) {
                continue; // not a world document — some other blob sharing the puck/worlds/ prefix, not this discovery's concern.
            }

            var candidateId = fileName[..^WorldOwnedWorldFileName.Suffix.Length];

            if (!WorldSafeName.TryParse(
                candidate: candidateId,
                name: out _,
                reason: out var reason
            )) {
                refusals.Add(item: new WorldSyncOutcome(
                    Detail: $"no owned-world id can address this cloud object — its name {reason}, so something that does not share this catalog's mapping wrote it; re-upload it under a safe name",
                    Id: fileName,
                    Ok: false
                ));
                continue;
            }

            ids.Add(item: candidateId);
        }
    }
    // The text is arbitrary, so the cut can land between a surrogate pair's halves; back off one char rather than
    // echo a lone surrogate.
    private static string FlattenDetail(string message) {
        var flat = message.ReplaceLineEndings(replacementText: " ");

        if (flat.Length <= DiscoveryDetailLengthLimit) {
            return flat;
        }

        var cut = (char.IsHighSurrogate(c: flat[(DiscoveryDetailLengthLimit - 1)])
            ? (DiscoveryDetailLengthLimit - 1)
            : DiscoveryDetailLengthLimit
        );

        return $"{flat[..cut]}…";
    }
    /// <summary>Parses a candidate id into a <see cref="WorldSafeName"/>, refusing by name (naming the offending
    /// character) exactly like every other door in this family — the id arrives here untyped (a console-verb
    /// argument, a sidecar-tracked key, or a candidate <see cref="DiscoverCloudIds"/> extracted from a cloud blob
    /// name), so this is the one place left that still validates rather than trusts. Once parsed, two distinct safe
    /// ids can never collide on one cloud key — <see cref="WorldOwnedWorldFileName"/>'s mapping is injective over
    /// <see cref="WorldSafeName"/> — so there is no separate "shares a key with a stranger" check left to run.</summary>
    private static string? KeyRefusal(string id, out WorldSafeName safe) {
        if (!WorldSafeName.TryParse(
            candidate: id,
            name: out safe,
            reason: out var reason
        )) {
            // No pipe in this text: the verb joins outcome lines with one.
            return $"its id {reason}";
        }

        return null;
    }
    private void LoadState() {
        try {
            if (!File.Exists(path: m_stateFilePath)) {
                return;
            }
            using var stateDocument = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: m_stateFilePath));
            var root = stateDocument.RootElement;

            if (
                root.TryGetProperty(
                propertyName: "worlds",
                value: out var worlds
            ) &&
                (worlds.ValueKind == JsonValueKind.Object)
            ) {
                foreach (var entry in worlds.EnumerateObject()) {
                    if (entry.Value.GetString() is { } token) {
                        m_tokens[key: entry.Name] = token;
                    }
                }
            }
            if (
                root.TryGetProperty(
                propertyName: "basis",
                value: out var basis
            ) &&
                (basis.ValueKind == JsonValueKind.Object)
            ) {
                foreach (var entry in basis.EnumerateObject()) {
                    if (entry.Value.GetString() is { } token) {
                        m_basisTokens[key: entry.Name] = token;
                    }
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException)) {
            Console.Error.WriteLine(value: $"[storage] sync state unreadable, starting untracked ({exception.Message})");
            m_basisTokens.Clear();
            m_tokens.Clear();
            m_lastSyncedRevision = 0;
        }
    }
    private WorldSyncOutcome PullOne(string id) {
        if (KeyRefusal(
            id: id,
            safe: out var safe
        ) is { } keyRefusal) {
            return new WorldSyncOutcome(
                Detail: keyRefusal,
                Id: id,
                Ok: false
            );
        }

        ObjectBlobContent content;

        try {
            using var timeout = new CancellationTokenSource(delay: OperationTimeout);
            var read = m_store.ReadAsync(
                address: AddressFor(
                    containerId: m_containerId,
                    id: safe
                ),
                cancellationToken: timeout.Token,
                target: m_target
            ).AsTask().GetAwaiter().GetResult();

            if (read is not { } found) {
                return new WorldSyncOutcome(
                    Detail: "no cloud copy",
                    Id: id,
                    Ok: false
                );
            }
            content = found;
        } catch (OperationCanceledException) {
            return new WorldSyncOutcome(
                Id: id,
                Ok: false,
                Detail: $"timed out after {OperationTimeout.TotalSeconds:0}s"
            );
        } catch (Exception exception) {
            return new WorldSyncOutcome(
                Id: id,
                Ok: false,
                Detail: $"transport error — {exception.Message}"
            );
        }

        // The boot loader's gate (strict parse + validation) decides admission; a temp file that never matches the
        // catalog's *.world.json glob carries the bytes through it. Reuses this engine's OWN store/target/container
        // (already resolved, since a pull cannot run without them) to prove the pulled document's own adjacency
        // claims — a genuine document LOAD, the same "settled once, not on the tick path" case WorldServer.Neighbours'
        // own remarks describe, and cheap here: no extra wiring, the triple already sits on this instance.
        var neighbours = new WorldStorageNeighbourResolver(
            containerId: m_containerId,
            store: m_store,
            target: m_target
        );
        var path = Path.Combine(
            path1: m_worlds.FilePath,
            path2: WorldOwnedWorldFileName.For(id: safe)
        );
        var probePath = $"{path}.pull-probe";

        JsonObject? composed;
        IReadOnlyList<byte[]> chainBytes;

        try {
            using var timeout = new CancellationTokenSource(delay: OperationTimeout);

            if (!WorldDefinitionFileSource.TryComposeChain(
                source: new WorldStorageDocumentSource(
                    cancellationToken: timeout.Token,
                    containerId: m_containerId,
                    store: m_store,
                    target: m_target
                ),
                // Seeded from the TIP's own blob key, not its bare file name: a basis link's own resolvedName lives
                // in the SAME flat namespace (see WorldStorageDocumentSource.TryRead), and a basis document that
                // happens to share the tip's bare name would otherwise read as a cycle back to the root.
                rootResolvedName: AddressFor(
                    containerId: m_containerId,
                    id: safe
                ).Key,
                rootBytes: content.Content.ToArray(),
                composed: out composed,
                chainBytes: out chainBytes,
                reason: out var composeReason
            )) {
                return new WorldSyncOutcome(
                    Detail: $"cloud copy's basis chain refused: {composeReason}",
                    Id: id,
                    Ok: false
                );
            }
        } catch (OperationCanceledException) {
            return new WorldSyncOutcome(
                Id: id,
                Ok: false,
                Detail: $"basis chain compose timed out after {OperationTimeout.TotalSeconds:0}s"
            );
        }

        // A flat cloud copy (composed is null) writes its own bytes unchanged, exactly as before this seam existed;
        // a chain-bearing copy writes the FLATTENED composition, so the probe — and the boot gate it crosses — sees
        // a self-contained document either way.
        var probeBytes = ((composed is not null)
            ? Encoding.UTF8.GetBytes(s: composed.ToJsonString())
            : content.Content.ToArray()
        );

        try {
            File.WriteAllBytes(
                bytes: probeBytes,
                path: probePath
            );
            if (
                !WorldDefinitionFileSource.TryLoad(
                contentHash: out _,
                definition: out var document,
                neighbours: neighbours,
                path: probePath,
                reason: out var reason
            ) ||
                (document is null)
            ) {
                return new WorldSyncOutcome(
                    Detail: $"cloud copy refused by the document gate — {reason}",
                    Id: id,
                    Ok: false
                );
            }
            // The key was chosen from the REQUESTED id and the token is filed under it, but adoption keys on the
            // DOCUMENT's id. Letting the two differ adopts one world under another's name: the local world the
            // document names is overwritten, the token lands on an id nothing will ever push, and every later
            // whole-catalog pull repeats it.
            if (
                (document.Identity is { } incoming) &&
                !string.Equals(
                a: incoming.Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return new WorldSyncOutcome(
                    Id: id,
                    Ok: false,
                    Detail: $"the cloud object '{WorldsNamespace}/{WorldOwnedWorldFileName.For(id: safe)}' holds a document declaring id '{incoming.Id}', not '{id}' — adopting it would file it under one id and track it under another; push it to its own key, or rename its identity to '{id}'"
                );
            }
            // A chain-derived cloud copy adopts as a DELTA, never a flattened document: each ancestor lands in this
            // catalog's own basis/ directory and the tip is written with its basis member rewritten to the local
            // spelling, so ReplaceFromSync's own Save (SavePreservingBasis) recomposes and diffs against a basis
            // chain that already exists on disk — the same derivation-preserving path a local save runs. Written
            // BEFORE adoption so a fresh machine's very first pull of a delta never lands a flat file that the next
            // push would overwrite the cloud's own authored delta with.
            if (
                (composed is not null) &&
                (chainBytes.Count > 1) &&
                !TryWriteChainLinksLocally(
                chainBytes: chainBytes,
                path: path,
                reason: out var linkReason
            )
            ) {
                return new WorldSyncOutcome(
                    Detail: $"local write failed — {linkReason}",
                    Id: id,
                    Ok: false
                );
            }
            if (!m_worlds.ReplaceFromSync(
                document: document,
                reason: out var adoption
            )) {
                return new WorldSyncOutcome(
                    Detail: $"cloud copy refused — {adoption}",
                    Id: id,
                    Ok: false
                );
            }
            m_tokens[key: id] = (content.VersionToken ?? string.Empty);
            return new WorldSyncOutcome(
                Id: id,
                Ok: true,
                Detail: $"pulled — {adoption} (token {Abbreviate(token: content.VersionToken)})"
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return new WorldSyncOutcome(
                Id: id,
                Ok: false,
                Detail: $"local write failed — {exception.Message}"
            );
        } finally {
            try { File.Delete(path: probePath); } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) { }
        }
    }
    // A basis link's first-contact rule cannot be a wedge the way an owned world's can: two owned worlds can share
    // ONE basis file, and the second push to touch it this SESSION (whether the same Push() call, via
    // basisPushedThisCall, or a later one) has no token to present yet if it already landed. Nor can a STALE tracked
    // token be a wedge: storage.pull never adopts a basis blob's token (it only reads through the chain, never
    // writes it back — see TryWriteChainLinksLocally), so a token this catalog tracked can go stale with no local
    // path that ever refreshes it. Both a CreateOnly loss (untracked) and an if-match loss (tracked-but-stale) are
    // therefore read back and compared byte-for-byte — identical content adopts the current token; only genuine
    // divergence refuses.
    private WorldSyncOutcome PushBasisLink(string name, byte[] bytes, out WorldSyncWriteOutcome write) {
        write = WorldSyncWriteOutcome.Failed;

        var label = $"{name} (basis)";
        var address = BasisAddressFor(
            containerId: m_containerId,
            name: name
        );
        var tracked = m_basisTokens.TryGetValue(
            key: name,
            value: out var token
        );

        try {
            using var timeout = new CancellationTokenSource(delay: OperationTimeout);
            var result = m_store.WriteAsync(
                address: address,
                cancellationToken: timeout.Token,
                content: bytes,
                ifMatchVersion: (tracked
                ? token
                : null),
                mode: (tracked
                ? ObjectBlobWriteMode.Overwrite
                : ObjectBlobWriteMode.CreateOnly),
                target: m_target
            ).AsTask().GetAwaiter().GetResult();

            if (result.Succeeded) {
                write = WorldSyncWriteOutcome.Ok;
                m_basisTokens[key: name] = (result.VersionToken ?? string.Empty);
                return new WorldSyncOutcome(
                    Id: label,
                    Ok: true,
                    Detail: $"pushed (token {Abbreviate(token: result.VersionToken)})"
                );
            }

            if (
                (!tracked && !result.PreconditionFailed) ||
                (tracked && result.PreconditionFailed)
            ) {
                return ReconcileBasisCreateOnlyLoss(
                    address: address,
                    bytes: bytes,
                    label: label,
                    name: name,
                    write: out write
                );
            }

            write = (result.PreconditionFailed
                ? WorldSyncWriteOutcome.PreconditionFailed
                : WorldSyncWriteOutcome.Failed
            );
            return new WorldSyncOutcome(
                Id: label,
                Ok: false,
                Detail: (result.PreconditionFailed
                ? "the cloud copy moved since last sync"
                : "a cloud copy exists this catalog has never synced")
            );
        } catch (OperationCanceledException) {
            return new WorldSyncOutcome(
                Id: label,
                Ok: false,
                Detail: $"timed out after {OperationTimeout.TotalSeconds:0}s"
            );
        } catch (Exception exception) {
            return new WorldSyncOutcome(
                Id: label,
                Ok: false,
                Detail: $"transport error — {exception.Message}"
            );
        }
    }
    // The document write is the primary effect; a claim-post failure is reported, never fatal to the push. document
    // names this world under the exact spelling a peer's WorldReference.NeighbourKey computes for an owner-named
    // reference to it (ValidateAttestedCounterpart refuses any other spelling), never a filename.
    private string PublishCounterpartClaim(WorldDefinition document, string worldId) {
        if (m_publisher is null) {
            return "no counterpart publisher configured";
        }

        if (!WorldCounterpartAttestation.TryCompose(
            attestation: out var counterpart,
            definition: document,
            document: $"owner/{m_containerId:D}/{worldId}",
            reason: out var composeReason
        ) ||
            (counterpart is null)
        ) {
            return $"counterpart claim compose refused — {composeReason}";
        }

        var payload = WorldCounterpartAttestationProtocol.Payload(attestation: counterpart);

        return (m_publisher.TryPublish(
            detail: out var publishDetail,
            payload: payload,
            worldId: worldId
        )
            ? $"counterpart claim posted — {publishDetail}"
            : $"counterpart claim post refused — {publishDetail}");
    }
    // The tip push (chain[0]) and each basis link (chain[1..]) share one worst-of ordering; PushOne folds them
    // together so a caller sees ONE write outcome per identity regardless of how many blobs its chain touched.
    private WorldSyncOutcome PushOne(WorldIdentity identity, HashSet<string> basisPushedThisCall, List<WorldSyncOutcome> basisOutcomes, out WorldSyncWriteOutcome write) {
        write = WorldSyncWriteOutcome.Failed;

        if (
            (identity.Document is not { } document) ||
            (document.Identity is not { } identitySection)
        ) {
            return new WorldSyncOutcome(
                Id: identity.Id,
                Ok: false,
                Detail: "no document to push"
            );
        }

        // Live state, not stale disk state: re-save (basis-preserving) before resolving the chain, so the file on
        // disk and identity.Document agree — a push must publish what is LIVE, never what was last saved.
        m_worlds.Save(identity: identity);

        var safe = identitySection.Id;
        var path = Path.Combine(
            path1: m_worlds.FilePath,
            path2: WorldOwnedWorldFileName.For(id: safe)
        );

        if (
            !WorldDefinitionFileSource.TryResolveChainFiles(
            chain: out var chain,
            path: path,
            reason: out var chainReason
        ) ||
            (chain.Count == 0)
        ) {
            return new WorldSyncOutcome(
                Id: identity.Id,
                Ok: false,
                Detail: $"no document to push — {chainReason}"
            );
        }

        var content = chain[index: 0].Bytes;
        var tracked = m_tokens.TryGetValue(
            key: identity.Id,
            value: out var token
        );
        WorldSyncOutcome tipOutcome;

        try {
            using var timeout = new CancellationTokenSource(delay: OperationTimeout);
            var result = m_store.WriteAsync(
                address: AddressFor(
                    containerId: m_containerId,
                    id: safe
                ),
                cancellationToken: timeout.Token,
                content: content,
                ifMatchVersion: (tracked
                ? token
                : null),
                mode: (tracked
                ? ObjectBlobWriteMode.Overwrite
                : ObjectBlobWriteMode.CreateOnly),
                target: m_target
            ).AsTask().GetAwaiter().GetResult();

            if (result.Succeeded) {
                write = WorldSyncWriteOutcome.Ok;
                m_tokens[key: identity.Id] = (result.VersionToken ?? string.Empty);

                var claimDetail = PublishCounterpartClaim(
                    document: document,
                    worldId: identitySection.Id
                );

                m_lastClaimDetail = claimDetail;
                tipOutcome = new WorldSyncOutcome(
                    Id: identity.Id,
                    Ok: true,
                    Detail: $"pushed (token {Abbreviate(token: result.VersionToken)}); {claimDetail}"
                );
            } else {
                write = (result.PreconditionFailed
                    ? WorldSyncWriteOutcome.PreconditionFailed
                    : WorldSyncWriteOutcome.Failed
                );
                tipOutcome = new WorldSyncOutcome(
                    Id: identity.Id,
                    Ok: false,
                    Detail: (result.PreconditionFailed
                    ? "the cloud copy moved since last sync — storage.pull takes it; a push after that carries the fresh token"
                    : "a cloud copy exists this catalog has never synced — storage.pull first")
                );
            }
        } catch (OperationCanceledException) {
            tipOutcome = new WorldSyncOutcome(
                Id: identity.Id,
                Ok: false,
                Detail: $"timed out after {OperationTimeout.TotalSeconds:0}s"
            );
        } catch (Exception exception) {
            tipOutcome = new WorldSyncOutcome(
                Id: identity.Id,
                Ok: false,
                Detail: $"transport error — {exception.Message}"
            );
        }

        for (var index = 1; (index < chain.Count); index++) {
            var (name, bytes) = chain[index];

            if (!basisPushedThisCall.Add(item: name)) {
                continue; // already pushed by an earlier identity's chain THIS call.
            }

            var basisOutcome = PushBasisLink(
                bytes: bytes,
                name: name,
                write: out var basisWrite
            );

            basisOutcomes.Add(item: basisOutcome);
            if (basisWrite > write) {
                write = basisWrite;
            }
        }

        return tipOutcome;
    }
    private WorldSyncOutcome ReconcileBasisCreateOnlyLoss(ObjectBlobAddress address, byte[] bytes, string label, string name, out WorldSyncWriteOutcome write) {
        write = WorldSyncWriteOutcome.Failed;

        ObjectBlobContent? existing;

        try {
            using var readTimeout = new CancellationTokenSource(delay: OperationTimeout);

            existing = m_store.ReadAsync(
                address: address,
                cancellationToken: readTimeout.Token,
                target: m_target
            ).AsTask().GetAwaiter().GetResult();
        } catch (Exception exception) {
            return new WorldSyncOutcome(
                Id: label,
                Ok: false,
                Detail: $"'{address.Key}' exists in the cloud and could not be read to compare — {exception.Message.ReplaceLineEndings(replacementText: " ")}"
            );
        }

        if (
            (existing is { } found) &&
            found.Content.Span.SequenceEqual(other: bytes)
        ) {
            write = WorldSyncWriteOutcome.Ok;
            m_basisTokens[key: name] = (found.VersionToken ?? string.Empty);
            return new WorldSyncOutcome(
                Id: label,
                Ok: true,
                Detail: $"already present with identical content, adopted token {Abbreviate(token: found.VersionToken)}"
            );
        }

        return new WorldSyncOutcome(
            Id: label,
            Ok: false,
            Detail: $"'{address.Key}' exists in the cloud with different content — refusing to overwrite; resolve the divergence by hand"
        );
    }
    private void SaveState() {
        try {
            var swapPath = $"{m_stateFilePath}.swap";

            using (var stream = File.Create(path: swapPath))
            using (var writer = new Utf8JsonWriter(
                utf8Json: stream,
                options: new JsonWriterOptions { Indented = true }
            )) {
                writer.WriteStartObject();
                writer.WriteStartObject(propertyName: "worlds");
                foreach (var (id, token) in m_tokens.OrderBy(
                    keySelector: static entry => entry.Key,
                    comparer: StringComparer.Ordinal
                )) {
                    writer.WriteString(
                        propertyName: id,
                        value: token
                    );
                }
                writer.WriteEndObject();
                writer.WriteStartObject(propertyName: "basis");
                foreach (var (name, token) in m_basisTokens.OrderBy(
                    keySelector: static entry => entry.Key,
                    comparer: StringComparer.Ordinal
                )) {
                    writer.WriteString(
                        propertyName: name,
                        value: token
                    );
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            File.Move(
                destFileName: m_stateFilePath,
                overwrite: true,
                sourceFileName: swapPath
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[storage] sync state not persisted ({exception.Message})");
        }
    }
    private List<WorldIdentity> SelectIdentities(string? id) {
        if (id is null) {
            return [.. m_worlds.All];
        }
        return ((m_worlds.FindById(id: id) is { } identity)
            ? [identity]
            : []
        );
    }
    private SortedSet<string> SelectIds(string? id) {
        if (id is not null) {
            return new SortedSet<string>(comparer: StringComparer.Ordinal) { id };
        }
        var ids = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var identity in m_worlds.All) {
            ids.Add(item: identity.Id);
        }
        foreach (var tracked in m_tokens.Keys) {
            ids.Add(item: tracked);
        }
        return ids;
    }
    // Chain link names are read straight off each predecessor's OWN 'basis' member — the same bare canonical
    // spelling WorldStorageDocumentSource.TryRead resolved it under — never re-derived by any other means.
    // chainBytes[0] is the tip, chainBytes[1..] are ancestors in resolution order (see TryComposeChain).
    private static bool TryWriteChainLinksLocally(IReadOnlyList<byte[]> chainBytes, string path, out string reason) {
        reason = string.Empty;

        var basisDirectory = Path.Combine(
            path1: (Path.GetDirectoryName(path: path) ?? "."),
            path2: "basis"
        );

        try {
            Directory.CreateDirectory(path: basisDirectory);

            var names = new string[(chainBytes.Count - 1)];

            for (var index = 0; (index < names.Length); index++) {
                var link = ((JsonObject)JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: chainBytes[index]))!);

                names[index] = link[propertyName: WorldDocumentBasis.BasisMemberName]!.GetValue<string>();
            }

            for (var index = 1; (index < chainBytes.Count); index++) {
                File.WriteAllBytes(
                    bytes: chainBytes[index],
                    path: Path.Combine(
                        path1: basisDirectory,
                        path2: names[(index - 1)]
                    )
                );
            }

            var tip = ((JsonObject)JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: chainBytes[0]))!);

            tip[propertyName: WorldDocumentBasis.BasisMemberName] = $"basis/{names[0]}";
            File.WriteAllBytes(
                bytes: Encoding.UTF8.GetBytes(s: tip.ToJsonString()),
                path: path
            );

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException)) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }

    /// <summary>Computes the blob address an owned world's id addresses — the one writer-side encoding every
    /// push/pull goes through, and the address a test seeding a fake store for a resolver test should call rather
    /// than recompute independently: seeding at a hand-spelled key that happens to match today's reader encoding
    /// would stay green even if that encoding drifted, since the seed and the read would drift together.</summary>
    /// <param name="containerId">The per-user container id.</param>
    /// <param name="id">The owned world id.</param>
    /// <returns>The blob address.</returns>
    public static ObjectBlobAddress AddressFor(Guid containerId, WorldSafeName id) => new(
        ObjectId: containerId,
        Key: $"{WorldsNamespace}/{WorldOwnedWorldFileName.For(id: id)}"
    );
    /// <summary>Computes the blob address a basis-chain link's bare file name addresses — the ONE writer-side
    /// encoding every basis push/read goes through, under its own <c>basis/</c> namespace segment so
    /// <see cref="DiscoverCloudIds"/> can never mistake a chain link for a pullable owned world. <paramref
    /// name="name"/> is already canonical (see <see cref="WorldStorageDocumentSource"/>'s own name check) — this
    /// method does not re-validate it.</summary>
    /// <param name="containerId">The per-user container id.</param>
    /// <param name="name">The chain link's own canonical file name.</param>
    /// <returns>The blob address.</returns>
    public static ObjectBlobAddress BasisAddressFor(Guid containerId, string name) => new(
        Key: $"{BasisNamespace}/{name}",
        ObjectId: containerId
    );
    /// <summary>Pulls one owned world (or every local/tracked/cloud-discovered one when <paramref name="id"/> is null)
    /// from the cloud, validating each through the boot loader's gate before adopting it. A whole-catalog pull lists
    /// the cloud <c>puck/worlds/</c> namespace first and folds in any id it does not already know, so a cloud-only world
    /// reaches a fresh machine without the operator naming it; a listed blob whose name cannot round-trip back to an
    /// id refuses by name instead (see the class summary) and never silently drops out.</summary>
    /// <param name="id">The owned world id, or <see langword="null"/> for every local, tracked, and cloud-discovered
    /// id.</param>
    /// <returns>One outcome per attempted world, with a discovery refusal (if any) leading the list.</returns>
    public IReadOnlyList<WorldSyncOutcome> Pull(string? id) {
        var ids = SelectIds(id: id);
        var discoveryRefusals = new List<WorldSyncOutcome>();

        if (id is null) {
            DiscoverCloudIds(
                ids: ids,
                refusals: discoveryRefusals
            );
        }
        if (
            (ids.Count == 0) &&
            (discoveryRefusals.Count == 0)
        ) {
            return [new WorldSyncOutcome(
                    Detail: "no owned, tracked, or cloud-discovered worlds",
                    Id: (id ?? "*"),
                    Ok: false
                )];
        }

        var outcomes = new List<WorldSyncOutcome>(capacity: (ids.Count + discoveryRefusals.Count));

        outcomes.AddRange(collection: discoveryRefusals);
        foreach (var worldId in ids) {
            outcomes.Add(item: PullOne(id: worldId));
        }

        if (
            (id is null) &&
            outcomes.TrueForAll(match: outcome => outcome.Ok)
        ) {
            m_lastSyncedRevision = m_worlds.Revision;
        }
        SaveState();
        return outcomes;
    }
    /// <summary>Pushes one owned world (or every one when <paramref name="id"/> is null) to the cloud.</summary>
    /// <param name="id">The owned world id, or <see langword="null"/> for the whole catalog.</param>
    /// <returns>One outcome per attempted world.</returns>
    public IReadOnlyList<WorldSyncOutcome> Push(string? id) {
        var identities = SelectIdentities(id: id);

        if (identities.Count == 0) {
            return [new WorldSyncOutcome(
                    Detail: ((id is null)
                ? "no owned worlds"
                : "unknown owned world"),
                    Id: (id ?? "*"),
                    Ok: false
                )];
        }

        var outcomes = new List<WorldSyncOutcome>(capacity: identities.Count);
        var basisOutcomes = new List<WorldSyncOutcome>();
        // A basis shared by two identities in this SAME call pushes exactly once — the second identity's chain
        // finds its own link already claimed and skips it.
        var basisPushedThisCall = new HashSet<string>(comparer: StringComparer.Ordinal);
        // The worst thing that happened to any world (or basis link) in this push, in that order: a plain failure
        // outranks a precondition, because a run carrying both is not one storage.pull will settle.
        var write = WorldSyncWriteOutcome.Ok;

        foreach (var identity in identities) {
            outcomes.Add(item: PushOne(
                basisOutcomes: basisOutcomes,
                basisPushedThisCall: basisPushedThisCall,
                identity: identity,
                write: out var one
            ));

            if (one > write) {
                write = one;
            }
        }

        outcomes.AddRange(collection: basisOutcomes);

        m_lastWrite = write;
        if (
            (id is null) &&
            outcomes.TrueForAll(match: outcome => outcome.Ok)
        ) {
            m_lastSyncedRevision = m_worlds.Revision;
        }
        SaveState();
        return outcomes;
    }
}
