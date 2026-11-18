using Puck.Assets;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Machines;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// <c>world.reload</c>'s console half: compiles and admits the current source, resolves its text assets and diffs it
/// against the installed declarations on the thread pool, then submits the delta from the host pump. The authority
/// prepares and installs it at a tick boundary, so neither half runs on the tick.
/// </summary>
/// <remarks>The capture (the installed declarations, their version, the proof transport and the document source) is
/// taken on the pump when the verb runs; the preparation reads nothing else. The issuing text session holds its later
/// lines until the authority's verdict, so a following read sees the reloaded world. Text assets resolve through a
/// catalog of the preparations' own, since the presentation's font cache belongs to the pump.</remarks>
/// <param name="server">The authority whose installed declarations the delta names.</param>
/// <param name="link">The console's link to that authority.</param>
/// <param name="echoes">The pending-verb table the authority's verdict answers through.</param>
/// <param name="definitionSource">The source the running world was loaded from.</param>
/// <param name="machineCatalog">The machine catalog admission validates against.</param>
/// <param name="sourceWatch">The source watch that follows the reload's compiler inputs.</param>
internal sealed class WorldReload(WorldServer server, IServerLink link, WorldDeferredVerbEchoes echoes, WorldDefinitionSource definitionSource, WorldMachineCatalog machineCatalog, WorldSourceWatch sourceWatch) {
    private const string Verb = "world.reload";

    // The preparations' text catalog: its font cache is not thread-safe, so preparations take turns on it.
    private readonly Client.WorldTextCatalog m_textCatalog = new(source: definitionSource);
    private readonly Lock m_textGate = new();
    // Reloads whose preparation the pump has not yet taken; pump only.
    private readonly List<Pending> m_preparing = [];

    // A compiled, admitted, text-checked and diffed source, or the refusal preparation reached.
    private sealed record Prepared(string ContentHash, WorldReloadDelta? Delta, string? Refusal);

    // One reload in flight: the preparation the pump takes, then the authority's verdict it waits for.
    private sealed class Pending(WorldReload owner, CommandContext context, string path, WorldSourceWatch.Reads? reads) {
        private readonly CommandSettlement m_verdict = new();
        private BackgroundBuild<Prepared>? m_build;
        private CommandResult? m_refusal;

        public CommandSettlement Verdict => m_verdict;
        public CommandResult? Refusal => m_refusal;

        public void Start(Func<Prepared> prepare) {
            m_build = new BackgroundBuild<Prepared>();
            m_build.Start(build: _ => prepare());
        }
        // On the pump: submits a finished preparation. Returns whether the preparation is still running.
        public bool Advance() {
            if (m_build is not { } build) { return false; }
            if (!build.TryTake(result: out var prepared, error: out var error)) { return true; }
            m_build = null;
            var result = owner.Submit(context: context, path: path, prepared: (prepared ?? new Prepared(ContentHash: string.Empty, Delta: null, Refusal: error?.Message)), reads: reads);

            if (result.Settlement is { } settlement) {
                settlement.OnSettled(continuation: verdict => m_verdict.Settle(result: verdict));
            } else {
                m_refusal = result;
                context.Registry?.NoteDeferredRejection();
                m_verdict.Settle(result: result);
            }
            return false;
        }
        // The hold's predicate: holds the session until the verdict.
        public bool Holding() {
            _ = Advance();
            return !m_verdict.IsSettled;
        }
    }

    /// <summary>Starts a reload of the current source.</summary>
    /// <param name="context">The issuing line's context.</param>
    /// <returns>A result settling with the authority's verdict, or with the refusal preparation reached.</returns>
    public CommandResult Run(CommandContext context) {
        var path = definitionSource.SourcePath;
        var reads = sourceWatch.RecordReads(path: path);
        var pending = new Pending(owner: this, context: context, path: path, reads: reads);
        var basis = server.Configuration;
        var version = server.DocumentVersion;
        var documents = server.RebuildDocuments;
        var instanceIdentity = server.InstanceIdentity;
        var neighbours = server.ResolveRebuildNeighbours(path: path);

        pending.Start(prepare: () => Prepare(basis: basis, documents: documents, instanceIdentity: instanceIdentity, neighbours: neighbours, path: path, reads: reads, version: version));
        m_preparing.Add(item: pending);
        // The session's hold and the host step (Poll) both take the preparation, so it submits even once a caller's
        // answer deadline has ended the hold.
        context.TextSession?.HoldWhile(
            hold: pending.Holding,
            // A session that settles nothing prints the authority's verdict through the pending-verb table; only a
            // refusal reached before submission is news it would otherwise never see.
            released: _ => pending.Refusal
        );
        return CommandResult.Settling(settlement: pending.Verdict);
    }
    /// <summary>Submits every reload whose preparation has finished. Call on the host pump each step.</summary>
    public void Poll() {
        if (m_preparing.Count != 0) { _ = m_preparing.RemoveAll(match: static pending => !pending.Advance()); }
    }

    // On the thread pool: reads the source, admits it on its own facts and the proof transport, resolves its text
    // assets and diffs it against the captured declarations.
    private Prepared Prepare(string path, WorldSourceWatch.Reads? reads, WorldDefinition basis, WorldDocumentVersion version, IWorldDocumentSource? documents, string instanceIdentity, IWorldNeighbourResolver? neighbours) {
        bool Load(out WorldDefinitionAdmission? admission, out string contentHash, out string reason) => WorldDefinitionLoader.TryLoadFileForAdmission(
            keys: WorldPrivateDrawKeys.Preview,
            admission: out admission,
            catalog: machineCatalog,
            contentHash: out contentHash,
            documents: documents,
            instanceIdentity: instanceIdentity,
            neighbours: neighbours,
            path: path,
            reason: out reason
        );
        WorldDefinitionAdmission? admission;
        string contentHash;
        string reason;
        bool loadedSource;

        if (reads is null) {
            loadedSource = Load(admission: out admission, contentHash: out contentHash, reason: out reason);
        } else {
            using (CompileInputs.Record(log: reads.Log)) {
                loadedSource = Load(admission: out admission, contentHash: out contentHash, reason: out reason);
            }
        }
        if (!loadedSource) {
            return new Prepared(ContentHash: string.Empty, Delta: null, Refusal: reason);
        }

        var loaded = admission!.Definition;

        lock (m_textGate) {
            if (!m_textCatalog.TryValidate(definition: loaded, origin: path, reason: out reason)) {
                return new Prepared(ContentHash: contentHash, Delta: null, Refusal: $"text assets refused: {reason}");
            }
        }
        try {
            // The delta names installed declarations. A recipient export contains tick values instead, which are
            // neither this base nor what a reload installs. This verb is operator-only.
            return new Prepared(ContentHash: contentHash, Delta: WorldReloadDelta.Create(basis, version, WorldDrawBootResolver.FileInputs(loaded)), Refusal: null);
        } catch (InvalidDataException exception) {
            return new Prepared(ContentHash: contentHash, Delta: null, Refusal: exception.Message);
        }
    }
    // On the pump: adopts the reads, refuses what preparation refused and submits the delta.
    private CommandResult Submit(CommandContext context, string path, Prepared prepared, WorldSourceWatch.Reads? reads) {
        if (reads is not null) { sourceWatch.AdoptReads(reads: reads); }
        if (prepared.Refusal is { } refusal) { return sourceWatch.Refuse(diagnostic: refusal); }
        return link.SubmitRebuild(
            echoes: echoes,
            request: new WorldRebuildRequest(
                ContentHash: prepared.ContentHash,
                Definition: null,
                Delta: prepared.Delta,
                Force: false,
                Kind: WorldRebuildKind.Reload,
                Origin: new WorldRebuildOrigin.File(Path: path)
            ),
            principal: context.Principal,
            verb: Verb
        );
    }
}
