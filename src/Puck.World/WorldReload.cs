using Puck.Assets;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Machines;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// <c>world.reload</c>'s console half: compiles, composes, draws and settles the current source, resolves its text
/// assets and cuts the delta from the source revision the authority holds on the thread pool, then submits the delta
/// from the host pump. The authority merges it over the live document, validates the result once and installs it at a
/// tick boundary, so neither half runs on the tick.
/// </summary>
/// <remarks>The capture (the authority's source revision and the document source) is taken on the pump when the verb
/// runs; the preparation reads nothing else. A source that still composes to that revision's content pin is not parsed,
/// drawn or sent: the verb answers that it is unchanged. The issuing text session holds its later lines until the
/// authority's verdict, so a following read sees the reloaded world. Text assets resolve through a catalog of the
/// preparations' own, since the presentation's font cache belongs to the pump.</remarks>
/// <param name="server">The authority whose installed declarations the delta names.</param>
/// <param name="link">The console's link to that authority.</param>
/// <param name="echoes">The pending-verb table the authority's verdict answers through.</param>
/// <param name="definitionSource">The source the running world was loaded from.</param>
/// <param name="machineCatalog">The machine catalog admission validates against.</param>
/// <param name="sourceWatch">The source watch that follows the reload's compiler inputs.</param>
/// <param name="iterations">The edit-loop clock each reload begins a loop on.</param>
internal sealed class WorldReload(WorldServer server, IServerLink link, WorldDeferredVerbEchoes echoes, WorldDefinitionSource definitionSource, WorldMachineCatalog machineCatalog, WorldSourceWatch sourceWatch, WorldIterationClock iterations) {
    private const string Verb = "world.reload";

    // The preparations' text catalog: its font cache is not thread-safe, so preparations take turns on it.
    private readonly Client.WorldTextCatalog m_textCatalog = new(source: definitionSource);
    private readonly Lock m_textGate = new();
    // Reloads whose preparation the pump has not yet taken; pump only.
    private readonly List<Pending> m_preparing = [];

    // A compiled, composed, text-checked source and its delta from the authority's revision, a source unchanged since
    // that revision, or the refusal preparation reached.
    private sealed record Prepared(string ContentHash, WorldReloadDelta? Delta, string? Refusal) {
        // Whether the source still composes to the authority's revision, so nothing is sent.
        public bool Unchanged { get; init; }
        // Where the preparation's time went, in milliseconds: compiling the source, composing, drawing and settling
        // the compiled document, checking its text assets, and cutting the delta.
        public double CompileMilliseconds { get; init; }
        public double AdmitMilliseconds { get; init; }
        public double TextMilliseconds { get; init; }
        public double DiffMilliseconds { get; init; }
    }

    // One reload in flight: the preparation the pump takes, then the authority's verdict it waits for.
    private sealed class Pending(WorldReload owner, CommandContext context, string path, WorldSourceWatch.Reads? reads, long loop) {
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
            var result = owner.Submit(context: context, loop: loop, path: path, prepared: (prepared ?? new Prepared(ContentHash: string.Empty, Delta: null, Refusal: error?.Message)), reads: reads);

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
        var loop = iterations.Begin(name: Verb, named: false);
        var path = definitionSource.SourcePath;
        var reads = sourceWatch.RecordReads(path: path);
        var pending = new Pending(owner: this, context: context, loop: loop, path: path, reads: reads);
        var revision = server.SourceRevision;
        var documents = server.RebuildDocuments;
        var instanceIdentity = server.InstanceIdentity;

        pending.Start(prepare: () => Prepare(documents: documents, instanceIdentity: instanceIdentity, path: path, reads: reads, revision: revision));
        m_preparing.Add(item: pending);
        pending.Verdict.OnSettled(continuation: verdict => {
            if (verdict.IsError) { iterations.Refuse(number: loop, refusal: verdict.Output); }
        });
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

    // On the thread pool: reads the source as an edit (composed, drawn and settled, never admitted: the authority
    // validates what it installs once), resolves its text assets and cuts its delta from the captured revision.
    private Prepared Prepare(string path, WorldSourceWatch.Reads? reads, WorldSourceRevision revision, IWorldDocumentSource? documents, string instanceIdentity) {
        bool Load(out WorldDefinition? loaded, out string contentHash, out string reason) => WorldDefinitionLoader.TryLoadFileForEdit(
            keys: WorldPrivateDrawKeys.Preview,
            catalog: machineCatalog,
            contentHash: out contentHash,
            definition: out loaded,
            documents: documents,
            instanceIdentity: instanceIdentity,
            path: path,
            reason: out reason,
            unchangedFrom: ((revision.ContentHash.Length > 0) ? revision.ContentHash : null)
        );
        // The source's compile, apart from the load that composes what it compiled: the load reads the same compile
        // from the shared cache, so the two phases are timed apart and nothing compiles twice.
        void Compile() {
            if (path.EndsWith(value: ".puck", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                try {
                    _ = Puck.World.Transpiler.WorldCompileSession.Shared.TryCompile(compiled: out _, failure: out _, path: path);
                } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
                    // The load reports what it cannot read.
                }
            }
        }
        WorldDefinition? loaded;
        string contentHash;
        string reason;
        bool loadedSource;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        long compiled;

        if (reads is null) {
            Compile();
            compiled = System.Diagnostics.Stopwatch.GetTimestamp();
            loadedSource = Load(loaded: out loaded, contentHash: out contentHash, reason: out reason);
        } else {
            using (CompileInputs.Record(log: reads.Log)) {
                Compile();
                compiled = System.Diagnostics.Stopwatch.GetTimestamp();
                loadedSource = Load(loaded: out loaded, contentHash: out contentHash, reason: out reason);
            }
        }
        if (!loadedSource) {
            return new Prepared(ContentHash: string.Empty, Delta: null, Refusal: reason);
        }
        if (loaded is null) {
            return new Prepared(ContentHash: contentHash, Delta: null, Refusal: null) { Unchanged = true };
        }

        var admitted = System.Diagnostics.Stopwatch.GetTimestamp();

        lock (m_textGate) {
            if (!m_textCatalog.TryValidate(definition: loaded, origin: path, reason: out reason)) {
                return new Prepared(ContentHash: contentHash, Delta: null, Refusal: $"text assets refused: {reason}");
            }
        }
        var texted = System.Diagnostics.Stopwatch.GetTimestamp();

        try {
            // The delta is cut between two revisions of the source, never against the live document: the authority
            // merges it over whatever the session has changed since the revision it holds.
            var delta = WorldReloadDelta.Between(previous: revision.Definition, next: loaded);

            return new Prepared(ContentHash: contentHash, Delta: delta, Refusal: null) {
                AdmitMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: compiled, endingTimestamp: admitted).TotalMilliseconds,
                CompileMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: started, endingTimestamp: compiled).TotalMilliseconds,
                DiffMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: texted).TotalMilliseconds,
                TextMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: admitted, endingTimestamp: texted).TotalMilliseconds,
            };
        } catch (InvalidDataException exception) {
            return new Prepared(ContentHash: contentHash, Delta: null, Refusal: exception.Message);
        }
    }
    // On the pump: adopts the reads, refuses what preparation refused and submits the delta.
    private CommandResult Submit(CommandContext context, long loop, string path, Prepared prepared, WorldSourceWatch.Reads? reads) {
        if (reads is not null) { sourceWatch.AdoptReads(reads: reads); }
        if (prepared.Refusal is { } refusal) { return sourceWatch.Refuse(diagnostic: refusal); }
        if (prepared.Unchanged) {
            var unchanged = new CommandResult(Output: $"[world.reload: the source is unchanged since the revision the authority holds ({prepared.ContentHash})]");

            iterations.End(number: loop, reason: "the source is unchanged");
            sourceWatch.NoteResult(result: unchanged);
            return unchanged;
        }
        Console.Error.WriteLine(value: string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"[world.reload] source compile={prepared.CompileMilliseconds:0} ms load={prepared.AdmitMilliseconds:0} ms text={prepared.TextMilliseconds:0} ms delta={prepared.DiffMilliseconds:0} ms patch={(prepared.Delta?.Patch.Length ?? 0)} bytes"));
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
