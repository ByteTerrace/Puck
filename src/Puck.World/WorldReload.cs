using Puck.Assets;
using Puck.Commands;
using Puck.World.Machines;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// <c>world.reload</c>'s console half: the desktop's reader for the reload door (<see cref="WorldReloadDoor"/>). The
/// reader compiles, composes, draws and settles the current source and resolves its text assets on the thread pool;
/// the door cuts the delta from the source revision the authority holds and submits it from the host pump. The
/// authority merges it over the live document, validates the result once and installs it at a tick boundary, so
/// neither half runs on the tick.
/// </summary>
/// <remarks>The reader reads nothing but what the verb captured on the pump. A source that still composes to the
/// authority's revision is not parsed, drawn or sent: the verb answers that it is unchanged. The issuing text session
/// holds its later lines until the authority's verdict, so a following read sees the reloaded world. Text assets
/// resolve through a catalog of the reader's own, since the presentation's font cache belongs to the pump.</remarks>
/// <param name="server">The authority whose installed declarations the delta names.</param>
/// <param name="link">The console's link to that authority.</param>
/// <param name="echoes">The pending-verb table the authority's verdict answers through.</param>
/// <param name="definitionSource">The source the running world was loaded from.</param>
/// <param name="machineCatalog">The machine catalog the source composes against.</param>
/// <param name="sourceWatch">The source watch that follows the reload's compiler inputs.</param>
/// <param name="iterations">The edit-loop clock each reload begins a loop on.</param>
internal sealed class WorldReload(WorldServer server, IServerLink link, WorldDeferredVerbEchoes echoes, WorldDefinitionSource definitionSource, WorldMachineCatalog machineCatalog, WorldSourceWatch sourceWatch, WorldIterationClock iterations) {
    private const string Verb = "world.reload";

    private readonly WorldReloadDoor m_door = new(echoes: echoes, link: link, server: server);
    // The reader's text catalog: its font cache is not thread-safe, so readings take turns on it.
    private readonly Client.WorldTextCatalog m_textCatalog = new(source: definitionSource);
    private readonly Lock m_textGate = new();

    // Where one reading's time went, in milliseconds: compiling the source, composing, drawing and settling the
    // compiled document, and checking its text assets.
    private sealed class Phases {
        public double CompileMilliseconds;
        public double LoadMilliseconds;
        public double TextMilliseconds;
    }

    /// <summary>Starts a reload of the current source.</summary>
    /// <param name="context">The issuing line's context.</param>
    /// <returns>A result settling with the authority's verdict, or with the answer the reading reached.</returns>
    public CommandResult Run(CommandContext context) {
        var loop = iterations.Begin(name: Verb, named: false);
        var path = definitionSource.SourcePath;
        var reads = sourceWatch.RecordReads(path: path);
        var documents = server.RebuildDocuments;
        var instanceIdentity = server.InstanceIdentity;
        var phases = new Phases();
        var pending = m_door.Begin(
            origin: new WorldRebuildOrigin.File(Path: path),
            principal: context.Principal,
            read: (revision, _) => ValueTask.FromResult(result: Read(documents: documents, instanceIdentity: instanceIdentity, path: path, phases: phases, reads: reads, revision: revision)),
            // On the pump: adopts what the reading read, and reports where its time went before the delta is sent.
            taken: cut => {
                if (reads is not null) { sourceWatch.AdoptReads(reads: reads); }
                if (cut.Unchanged) {
                    iterations.End(number: loop, reason: "the source is unchanged");
                } else if (cut.Refusal is null) {
                    Console.Error.WriteLine(value: string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
                        handler: $"[world.reload] source compile={phases.CompileMilliseconds:0} ms load={phases.LoadMilliseconds:0} ms text={phases.TextMilliseconds:0} ms delta={cut.DeltaMilliseconds:0} ms patch={(cut.Delta?.Patch.Length ?? 0)} bytes"));
                }
                return null;
            },
            verb: Verb
        );

        pending.Verdict.OnSettled(continuation: verdict => {
            // An answer reached before submission is news no authority verdict reports: the editor's diagnostic
            // follows it, and a refusal counts as the line's own.
            if (pending.Unsubmitted is { } unsubmitted) {
                sourceWatch.NoteResult(result: unsubmitted);
                if (unsubmitted.IsError) { context.Registry?.NoteDeferredRejection(); }
            }
            if (verdict.IsError) { iterations.Refuse(number: loop, refusal: verdict.Output); }
        });
        // The session's hold and the host step (Poll) both take the reading, so it submits even once a caller's
        // answer deadline has ended the hold.
        context.TextSession?.HoldWhile(
            hold: pending.Holding,
            // A session that settles nothing prints the authority's verdict through the pending-verb table; only an
            // answer reached before submission is news it would otherwise never see.
            released: _ => pending.Unsubmitted
        );
        return CommandResult.Settling(settlement: pending.Verdict);
    }
    /// <summary>Submits every reload whose reading has finished. Call on the host pump each step.</summary>
    public void Poll() => m_door.Poll();

    // On the thread pool: reads the source as an edit (composed, drawn and settled, never admitted: the authority
    // validates what it installs once) and resolves its text assets.
    private WorldSourceRead Read(string path, WorldSourceWatch.Reads? reads, WorldSourceRevision revision, IWorldDocumentSource? documents, string instanceIdentity, Phases phases) {
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
        var stamps = CompileInputs.StampReads;
        var contents = CompileInputs.ContentReads;
        // The compile and the load ask about the same files: one pass reads each once.
        using var pass = CompileInputs.Pass();
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
        // What proving the sources and held compositions cost, through the one read door. Its counters are
        // process-wide, so a read another thread makes meanwhile counts here too.
        WorldBootWork.Count(amount: (CompileInputs.StampReads - stamps), kind: WorldBootWork.CompileStampReads);
        WorldBootWork.Count(amount: (CompileInputs.ContentReads - contents), kind: WorldBootWork.CompileContentReads);
        if (!loadedSource) { return new WorldSourceRead(ContentHash: string.Empty, Definition: null, Refusal: reason); }
        if (loaded is null) { return new WorldSourceRead(ContentHash: contentHash, Definition: null); }

        var admitted = System.Diagnostics.Stopwatch.GetTimestamp();

        lock (m_textGate) {
            if (!m_textCatalog.TryValidate(definition: loaded, origin: path, reason: out reason)) {
                return new WorldSourceRead(ContentHash: contentHash, Definition: null, Refusal: $"text assets refused: {reason}");
            }
        }
        phases.CompileMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: started, endingTimestamp: compiled).TotalMilliseconds;
        phases.LoadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: compiled, endingTimestamp: admitted).TotalMilliseconds;
        phases.TextMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: admitted).TotalMilliseconds;
        return new WorldSourceRead(ContentHash: contentHash, Definition: loaded);
    }
}
