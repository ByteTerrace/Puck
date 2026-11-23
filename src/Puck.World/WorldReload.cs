using Puck.Assets;
using Puck.Commands;
using Puck.World.Machines;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// <c>world.reload</c>'s console half: the desktop's reader for the reload door (<see cref="WorldReloadDoor"/>), over
/// every world running in this process. Each running row (the boot world and each instance a portal, painting or console
/// started beside it) reads its own source: the reader compiles, composes, draws and settles it and resolves its text
/// assets on the thread pool, and the row's door cuts the delta from the source revision that row's authority holds and
/// submits it through the row's own console link. Each authority merges its delta over its live document, validates the
/// result once and installs it at its own tick boundary, so no half runs on a tick, and an edit to any world's source
/// shows in the world it belongs to, wherever the seats stand.
/// </summary>
/// <remarks>The reader reads nothing but what the verb captured on the pump. A source that still composes to its
/// authority's revision is not parsed, drawn or sent: that row answers that it is unchanged. The issuing text session
/// holds its later lines until every row's verdict, so a following read sees the reloaded worlds. Text assets resolve
/// through a catalog of the reader's own, since the presentation's font cache belongs to the pump.</remarks>
/// <param name="instances">The rows a reload reaches: every running row, or the one a trailing <c>instance:</c> token
/// names.</param>
/// <param name="echoes">The pending-verb table each row's verdict answers through.</param>
/// <param name="definitionSource">The boot world's source, whose directory the reader's text catalog resolves
/// against.</param>
/// <param name="machineCatalog">The machine catalog every row's source composes against.</param>
/// <param name="sourceWatch">The source watch that follows each row's compiler inputs.</param>
/// <param name="iterations">The edit-loop clock each reload begins a loop on.</param>
internal sealed class WorldReload(WorldInstanceHost instances, WorldDeferredVerbEchoes echoes, WorldDefinitionSource definitionSource, WorldMachineCatalog machineCatalog, WorldSourceWatch sourceWatch, WorldIterationClock iterations) {
    private const string Verb = "world.reload";

    // Each running row's door, made the first time a reload reaches the row and dropped once the row retires; pump only.
    private readonly Dictionary<WorldInstance, WorldReloadDoor> m_doors = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<WorldInstance> m_retired = [];
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

    // One row's reload in flight: the row and its door's pending reload.
    private readonly record struct RowReload(WorldInstance Row, WorldReloadDoor.Pending Pending) {
        public bool Boot => WorldDefinitionLoader.IsBootInstance(name: Row.Name);
        // A row's answer as the verb prints it: the boot row's as it stands, another row's tagged with its name.
        public CommandResult Tagged(CommandResult result) => ((Boot || string.IsNullOrEmpty(value: result.Output))
            ? result
            : (result with { Output = CommandEcho.SpliceTag(prefix: WorldArgs.InstanceTokenPrefix, text: result.Output, value: Row.Name) }));
    }

    /// <summary>The verb's forms: every running row, or the one a trailing <c>instance:</c> token names.</summary>
    public static CommandForms Forms { get; } = CommandForms.Of(verb: Verb, $"[{WorldArgs.InstanceTokenPrefix}<name>]");

    /// <summary>Starts a reload of the rows a <c>world.reload</c> line addresses: every running row, or the row its
    /// trailing <c>instance:</c> token names.</summary>
    /// <param name="context">The issuing line's context.</param>
    /// <param name="args">The line's arguments: none, or one <c>instance:&lt;name&gt;</c> token.</param>
    /// <returns>A result settling once every addressed row answered, or the refusal of a malformed line.</returns>
    public CommandResult Run(CommandContext context, in WireArgs args) {
        if (args.Count == 0) { return Run(context: context, only: null); }
        if ((args.Count != 1) || !WorldArgs.IsInstanceToken(token: args[0])) { return Forms.Refuse(); }
        if (!WorldInstanceArgs.TryResolveInstance(error: out var error, instance: out var row, instances: instances, token: args[0], verb: Verb)) {
            return error!.Value;
        }
        return Run(context: context, only: row);
    }
    /// <summary>Starts a reload of every running row's source, or of one row's.</summary>
    /// <param name="context">The issuing line's context.</param>
    /// <param name="only">The one row to reload, or <see langword="null"/> for every running row.</param>
    /// <returns>A result settling once every row's authority answered, or its reading reached an answer of its own.</returns>
    public CommandResult Run(CommandContext context, WorldInstance? only = null) {
        var loop = iterations.Begin(name: Verb, named: false);
        var reloads = new List<RowReload>();

        Prune();
        if (only is not null) {
            reloads.Add(item: Begin(context: context, row: only));
        } else {
            if (instances.Boot is { } boot) { reloads.Add(item: Begin(context: context, row: boot)); }
            foreach (var name in instances.Names) {
                if (!WorldDefinitionLoader.IsBootInstance(name: name) && instances.TryGet(instance: out var row, name: name) && (row is not null)) {
                    reloads.Add(item: Begin(context: context, row: row));
                }
            }
        }

        var verdict = new CommandSettlement();
        var remaining = reloads.Count;

        foreach (var reload in reloads) {
            reload.Pending.Verdict.OnSettled(continuation: settled => {
                // An answer reached before submission is news no authority verdict reports: the editor's diagnostic
                // follows it, and a refusal counts as the line's own.
                if (reload.Pending.Unsubmitted is { } unsubmitted) {
                    sourceWatch.NoteResult(result: unsubmitted);
                    if (unsubmitted.IsError) { context.Registry?.NoteDeferredRejection(); }
                }
                if (Interlocked.Decrement(location: ref remaining) != 0) { return; }

                var combined = Combine(reloads: reloads, unsubmittedOnly: false)!.Value;

                if (combined.IsError) {
                    iterations.Refuse(number: loop, refusal: combined.Output);
                } else if (reloads.TrueForAll(match: static row => row.Pending.Unsubmitted is { IsError: false })) {
                    iterations.End(number: loop, reason: "the source is unchanged");
                }
                verdict.Settle(result: combined);
            });
        }
        // The session's hold and the host step (Poll) both take the readings, so each submits even once a caller's
        // answer deadline has ended the hold.
        context.TextSession?.HoldWhile(
            hold: () => {
                var holding = false;

                foreach (var reload in reloads) { holding |= reload.Pending.Holding(); }
                return holding;
            },
            // A session that settles nothing prints each submitted row's verdict through the pending-verb table; only the
            // answers reached before submission are news it would otherwise never see.
            released: _ => Combine(reloads: reloads, unsubmittedOnly: true)
        );
        return CommandResult.Settling(settlement: verdict);
    }
    /// <summary>Submits every reload whose reading has finished. Call on the host pump each step.</summary>
    public void Poll() {
        foreach (var door in m_doors.Values) { door.Poll(); }
    }

    // Drops the doors of rows that have retired, so a later row under the same name gets a door of its own.
    private void Prune() {
        foreach (var row in m_doors.Keys) {
            if (row.Retired.IsCancellationRequested) { m_retired.Add(item: row); }
        }
        foreach (var row in m_retired) { _ = m_doors.Remove(key: row); }
        m_retired.Clear();
    }
    // The verb's answer over every row: each row that changed or refused answers in its own line, and the rows still at
    // the revision their authority holds share one. With unsubmittedOnly, only what no authority verdict will print.
    private static CommandResult? Combine(List<RowReload> reloads, bool unsubmittedOnly) {
        if (reloads.Count == 1) {
            var only = reloads[0];

            return (unsubmittedOnly
                ? ((only.Pending.Unsubmitted is { } unsubmitted) ? only.Tagged(result: unsubmitted) : null)
                : only.Tagged(result: Settled(pending: only.Pending)));
        }

        var lines = new List<string>();
        var unchanged = new List<string>();
        var error = false;

        foreach (var reload in reloads) {
            if (reload.Pending.Unsubmitted is { IsError: false }) {
                unchanged.Add(item: reload.Row.Name);
                continue;
            }
            if (unsubmittedOnly && (reload.Pending.Unsubmitted is null)) { continue; }

            var result = reload.Tagged(result: Settled(pending: reload.Pending));

            error |= result.IsError;
            if (!string.IsNullOrEmpty(value: result.Output)) { lines.Add(item: result.Output); }
        }
        if ((unchanged.Count != 0) && (!unsubmittedOnly || (lines.Count != 0) || (unchanged.Count == reloads.Count))) {
            lines.Add(item: $"[{Verb}: unchanged since the revision each authority holds: {string.Join(separator: ", ", values: unchanged)}]");
        }
        return ((lines.Count == 0) ? null : new CommandResult(Output: string.Join(separator: '\n', values: lines)) { IsError = error });
    }
    // A settled row's verdict, read back from its settlement.
    private static CommandResult Settled(WorldReloadDoor.Pending pending) {
        var result = default(CommandResult);

        pending.Verdict.OnSettled(continuation: settled => result = settled);
        return result;
    }
    // Begins one row's reload on its own door, reading that row's own source.
    private RowReload Begin(CommandContext context, WorldInstance row) {
        if (!m_doors.TryGetValue(key: row, value: out var door)) {
            door = new WorldReloadDoor(echoes: echoes, link: row.SubmissionLink, server: row.Server);
            m_doors.Add(key: row, value: door);
        }

        var server = row.Server;
        var path = row.SourcePath;
        var reads = sourceWatch.RecordReads(path: path);
        var documents = server.RebuildDocuments;
        var instanceIdentity = server.InstanceIdentity;
        var phases = new Phases();
        var label = (WorldDefinitionLoader.IsBootInstance(name: row.Name) ? string.Empty : $" instance:{row.Name}");
        var pending = door.Begin(
            origin: new WorldRebuildOrigin.File(Path: path),
            principal: context.Principal,
            read: (revision, _) => ValueTask.FromResult(result: Read(documents: documents, instanceIdentity: instanceIdentity, path: path, phases: phases, reads: reads, revision: revision)),
            // On the pump: adopts what the reading read, and reports where its time went before the delta is sent.
            taken: cut => {
                if (reads is not null) { sourceWatch.AdoptReads(reads: reads); }
                if (!cut.Unchanged && (cut.Refusal is null)) {
                    Console.Error.WriteLine(value: string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
                        handler: $"[world.reload]{label} source compile={phases.CompileMilliseconds:0} ms load={phases.LoadMilliseconds:0} ms text={phases.TextMilliseconds:0} ms delta={cut.DeltaMilliseconds:0} ms patch={(cut.Delta?.Patch.Length ?? 0)} bytes"));
                }
                return null;
            },
            verb: Verb
        );

        return new RowReload(Pending: pending, Row: row);
    }

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
