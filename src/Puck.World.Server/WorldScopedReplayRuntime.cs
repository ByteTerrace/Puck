using Puck.Maths;
using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A scoped live recording, with an arena image and body continuations instead of a world boot image.
/// It never loads a world, changes a seat's routes, or suppresses another scope's live input.</summary>
public sealed partial class WorldScopedReplayRuntime {
    private readonly WorldServer m_server;
    private readonly WorldScopedReplay m_source;
    private readonly HashSet<int> m_bodies;
    private readonly HashSet<int> m_rows = [];

    private readonly List<(int Tick, WorldMutation Mutation)> m_inputs = [];
    private readonly List<ulong> m_hashes = [];
    private readonly List<(int Tick, WorldCommand Command)> m_commands = [];
    private readonly List<(int Tick, IntentSubmission Intent)> m_intents = [];

    /// <summary>The control row's <c>status</c> of a scope that holds no starting image: an edit re-declared one of its
    /// rows, or a request found no start or a start it could no longer restore. A capture request leaves it.</summary>
    public const int NoStartStatus = -2;

    private ArenaScopeSnapshot? m_start;

    private WorldPopulationEntryCheckpoint[] m_startBodies = [];

    private ulong m_startHash;
    private ulong m_startImageHash;
    private int m_cursor;
    private int m_inputCursor;
    private bool m_recording;
    private bool m_active;
    private bool m_diverged;

    private Fnv1aHash m_inputHash = Fnv1aHash.Create();
    private Fnv1aHash m_recordHash = Fnv1aHash.Create();
    private Fnv1aHash m_commandHash = Fnv1aHash.Create();
    private Fnv1aHash m_intentHash = Fnv1aHash.Create();

    private int[] m_orderedRows = [];

    /// <summary>Gets the authored scope identity.</summary>
    public string Name => m_source.Name;

    /// <summary>Binds an authored scope to its live arena. Admission validates row names and ownership.</summary>
    public WorldScopedReplayRuntime(WorldServer server, WorldScopedReplay source) {
        m_server = server;
        m_source = source;
        m_bodies = source.Bodies.ToHashSet();
        BindRows(arena: server.Arena);
    }

    // Resolves the scope's rows by name against an arena's catalog.
    private void BindRows(StateArena arena) {
        m_rows.Clear();
        foreach (var name in m_source.Rows) { m_rows.Add(item: Row(arena: arena, name: name)); }
        m_orderedRows = m_rows.Order().ToArray();
        m_allRules = null;
    }

    /// <summary>Carries the scope across an arena install: rebinds its rows by name, and keeps its starting image
    /// (carried by row and key name) and tape when the install is an edit that kept every scope row's declaration.
    /// Otherwise drops them; after an edit the control row reports the refusal (status -1) and stderr names it.</summary>
    /// <param name="previous">The arena the install replaces or reloaded.</param>
    /// <param name="arena">The arena the install adopts, before it is live.</param>
    /// <param name="continuity">The installing door's rule.</param>
    internal void Continue(StateArena previous, StateArena arena, WorldArenaContinuity continuity) {
        if ((continuity == WorldArenaContinuity.Edit) && ReferenceEquals(objA: previous, objB: arena)) { return; }
        var keeps = (continuity == WorldArenaContinuity.Edit);

        foreach (var name in m_source.Rows) {
            keeps = (keeps &&
                previous.Catalog.TryResolve(handle: out var before, lane: StateLane.Document, name: name) &&
                arena.Catalog.TryResolve(handle: out var after, lane: StateLane.Document, name: name) &&
                SameDeclaration(before: previous.Rows[before.Ordinal], after: arena.Rows[after.Ordinal]));
        }
        BindRows(arena: arena);
        var start = ((keeps && (m_start is not null)) ? arena.CarryScope(image: m_start, source: previous) : m_start);

        if (keeps && ((m_start is null) || (start is not null))) {
            if (!ReferenceEquals(objA: start, objB: m_start)) {
                m_start = start;
                m_startImageHash = WorldAuthorityCheckpointCodec.HashScopedReplayStart(scope: Checkpoint());
            }
            return;
        }
        var held = ((m_start is not null) || m_active || (m_hashes.Count != 0));

        DropStart();
        // A load reseeds the control row with the document; an edit leaves it naming a drive that no longer exists.
        if (held && (continuity == WorldArenaContinuity.Edit)) {
            Write(arena: arena, key: "status", value: NoStartStatus);
            Write(arena: arena, key: "cursor", value: 0);
            m_server.Output.Narrate("replay.scope", $"[replay.scope: {Name} refused — an edit re-declared a scope row; capture again]");
        }
    }

    // The one place a scope forgets its starting image, with the drive and tape measured from it.
    private void DropStart() {
        ResetDrive();
        ClearTape();
        m_start = null;
        m_startBodies = [];
        m_startHash = 0;
        m_startImageHash = 0;
        m_startRuleLatch = []; m_startInteractionLatch = []; m_startGroups = [];
    }

    private static bool SameDeclaration(StateRow before, StateRow after) => (
        ReferenceEquals(objA: before, objB: after) ||
        ((before is WorldStateRow left) && (after is WorldStateRow right) && WorldDocument.SameComposeDeclaration(previous: left, row: right))
    );

    private static int Row(StateArena arena, string name) => (arena.Catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: name)
        ? handle.Ordinal : throw new InvalidOperationException(message: $"No scope row '{name}'."));
    private static CellKey Key(StateArena arena, string name) => arena.Keys.Intern(name: CellName.Parse(candidate: name));
    private long Read(string key) {
        var arena = m_server.Arena;

        return (arena.TryRead(Row(arena: arena, name: m_source.Control), Key(arena: arena, name: key), out var value) ? value.Raw : 0);
    }
    private void Write(string key, long value) => Write(arena: m_server.Arena, key: key, value: value);
    private void Write(StateArena arena, string key, long value) {
        if (!arena.TryWrite(Row(arena: arena, name: m_source.Control), Key(arena: arena, name: key), value, StateWriteKind.Set, out var reason)) {
            throw new InvalidOperationException(message: reason);
        }
    }

    /// <summary>Creates a capture (4), drive or derive (1), cancel and restore (2), or restore (3) request.
    /// External callers submit it through their recorded transport door.</summary>
    public WorldMutation CreateRequest(int request, Principal principal) =>
        new WorldMutation.UpsertStateCell(principal, m_source.Control, "request", request, WorldDocumentWriteKind.Set);
    /// <summary>Reads the scope's own live values, independent of unrelated arena keys and bodies. The read takes the
    /// server's authority gate, which the cached fold of the rows is shared under.</summary>
    public ulong Hash() {
        lock (m_server.AuthorityGate) {
            var hash = RowsHash();

            hash.Add(value: WorldReplaySnapshot.HashState(m_server.Population, m_bodies));
            m_server.Population.AppendContinuationHash(bodies: m_bodies, hash: ref hash);
            AppendRuleHash(hash: ref hash);
            return hash.Value;
        }
    }

    // The scope's rows, each through the arena's own version-cached row digest.
    private Fnv1aHash RowsHash() {
        var arena = m_server.Arena;
        var hash = Fnv1aHash.Create();

        foreach (var row in m_orderedRows) { hash.Add(value: arena.RowDigest(rowOrdinal: row)); }
        return hash;
    }

    private void Capture() {
        var start = m_server.Arena.CaptureScope(rows: m_rows);
        var bodies = m_server.Population.Capture().Entries.Where(predicate: entry => m_bodies.Contains(item: entry.Index)).ToArray();

        if (bodies.Length != m_bodies.Count) { throw new InvalidOperationException(message: "A scoped body is absent."); }
        var hash = Hash();

        m_start = start;
        m_startBodies = bodies;
        m_startHash = hash;
        CaptureRules();
        m_startImageHash = WorldAuthorityCheckpointCodec.HashScopedReplayStart(scope: Checkpoint());
    }
    private void Restore() {
        if (m_start is null) { throw new InvalidOperationException(message: "Capture the scope's fresh starting state first."); }
        foreach (var image in m_startBodies) {
            if ((m_server.Body(index: image.Index) is null) || (m_server.Population.Generation(index: image.Index) != image.Generation)) {
                // A start a replaced body can no longer be restored to is no start: the scope holds none until it captures again.
                DropStart();
                throw new InvalidOperationException(message: "A scoped body was replaced; capture again.");
            }
        }
        // RestoreScope checks the whole image before it changes a column.
        m_server.Arena.RestoreScope(image: m_start);
        RestoreRules();
        // Every body is posed before any contact is restored: a pose passes the discontinuity door, which forgets the
        // contacts other bodies own with the posed one, so a contact restored earlier in the same pass would be lost.
        foreach (var image in m_startBodies) {
            var body = m_server.Body(index: image.Index)!;

            body.Pose(image.Position, image.Yaw, FixedQ4816.Zero, FixedQ4816.Zero);
            body.ApplyTransferState(state: image.DynamicState);
            body.ApplyIntegrationResidue(image.Residue, m_server.Population.ContactFieldVersion);
        }
        foreach (var image in m_startBodies) {
            m_server.Population.RestoreRigidContacts(images: image.RigidContacts, index: image.Index);
            m_server.Population.AdoptRestoredIsland(index: image.Index, rootPlusOne: image.Residue.RigidSleepRootPlusOne);
        }
        if (Hash() != m_startHash) { throw new InvalidOperationException(message: "Scope starting hash MISMATCH after restore."); }
        StopInput();
    }

    /// <summary>Runs at the input boundary, before the normal mutation drain.</summary>
    public void BeforeTick() {
        try { BeforeTickCore(); } catch (InvalidOperationException exception) {
            ResetDrive();
            Write(key: "status", value: ((m_start is null) ? NoStartStatus : -1));
            Write(key: "cursor", value: 0);
            m_server.Output.Narrate("replay.scope", $"[replay.scope: {Name} refused — {exception.Message}]");
        }
    }

    private void BeforeTickCore() {
        var request = Read(key: "request");

        if (request != 0) {
            Write(key: "request", value: 0);
            if (request == 4) {
                if (m_start is null) {
                    Capture();
                    // A capture ends a refusal for want of a start (NoStartStatus); the scope is idle again.
                    Write(key: "status", value: 0);
                    Write(key: "cursor", value: 0);
                }
            } else if (request is 2 or 3) {
                ResetDrive();
                Restore();
                Write(key: "status", value: 0);
                Write(key: "cursor", value: 0);
            } else if ((request == 1) && !m_active) {
                Restore();
                m_cursor = 0; m_inputCursor = 0; m_diverged = false;
                m_recording = (m_hashes.Count == 0);
                m_active = true;
                Write(key: "status", value: (m_recording ? 1 : 2));
                Write(key: "cursor", value: 0);
            }
        }
        if (!m_active) { return; }
        if (m_recording) {
            foreach (var input in m_source.Inputs) {
                if (input.Tick != m_cursor) { continue; }
                m_server.EnqueueMutation(
                    new WorldMutation.Batch(input.Principal, input.Cells.Select(selector: cell =>
                        ((WorldMutation)new WorldMutation.UpsertStateCell(input.Principal, cell.Row, cell.Key, cell.Value, WorldDocumentWriteKind.Set))).ToArray()),
                    connectionId: SubmissionEnvelope.ScopedReplayConnectionId);
            }
        } else {
            while ((m_inputCursor < m_inputs.Count) && (m_inputs[m_inputCursor].Tick == m_cursor)) {
                m_server.EnqueueMutation(m_inputs[m_inputCursor++].Mutation, connectionId: SubmissionEnvelope.ScopedReplayConnectionId);
            }
            foreach (var input in m_commands) { if (input.Tick == m_cursor) { m_server.ApplyCommand(input.Command); } }
            foreach (var input in m_intents) {
                if (input.Tick != m_cursor) { continue; }
                var recorded = input.Intent;
                var tick = m_server.NextInputTick;

                if (!m_server.Input.TryPublishSnapshot(recorded.Principal, recorded.EntityIndex, InputSource, recorded.Input.Image, tick,
                    out var owner, out var batch, out var reason, recorded.Input.Events.Span)) { throw new InvalidOperationException(message: reason); }
                // The scope is an authored producer on this timeline; queued batches own their observations.
                batch = batch with { Events = batch.Events.ToArray(), Contributions = batch.Contributions.ToArray() };
                if (m_server.Input.TryGet(recorded.Principal, out var primary)) {
                    m_server.Population.RestoreInputMapping(bodyIndex: recorded.EntityIndex, owner: primary!);
                }
                m_server.EnqueueIntent(submission: new(tick, recorded.EntityIndex, recorded.Intent, recorded.Principal, batch,
                    recorded.MeasuredHoldTicks, owner));
            }
        }
    }

    // Cancellation and refusal both leave a settled, checkpointable runtime. An interrupted first recording
    // has no complete expectation to replay, whereas a previously completed tape remains reusable.
    private void ResetDrive() {
        m_active = false;
        if (m_recording) { ClearTape(); }
        m_recording = false;
        m_cursor = 0; m_inputCursor = 0; m_diverged = false;
        StopInput();
    }
    private void ClearTape() {
        m_inputs.Clear(); m_hashes.Clear(); m_commands.Clear(); m_intents.Clear();
        m_recordHash = Fnv1aHash.Create(); m_inputHash = Fnv1aHash.Create();
        m_commandHash = Fnv1aHash.Create(); m_intentHash = Fnv1aHash.Create();
    }

    private string InputSource => ("$replay:" + Name);

    private void StopInput() {
        foreach (var actor in m_source.Seats) { m_server.Input.CancelSnapshots(actor: actor, source: InputSource); }
    }

    /// <summary>Records accepted player-door mutations, never their derived rule effects or a copy of the source.</summary>
    public void Observe(WorldMutation mutation) {
        if (!m_active || !m_recording || !m_source.Seats.Contains(value: mutation.Principal)) { return; }
        if (Within(mutation: mutation)) { m_inputs.Add(item: (m_cursor, mutation)); HashInput(mutation: mutation, tick: m_cursor); }
    }

    private bool Within(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertStateCell cell => m_source.Rows.Contains(value: cell.Row),
        WorldMutation.RemoveStateCell cell => m_source.Rows.Contains(value: cell.Row),
        WorldMutation.Batch batch => ((batch.Mutations.Count > 0) && batch.Mutations.All(predicate: child => ((child.Principal == batch.Principal) && Within(mutation: child)))),
        _ => false,
    };

    /// <summary>Records a body-addressed command at the normal drive door. Route and cross-body commands are outside
    /// this boundary; they require a wider authority scope.</summary>
    public void Observe(WorldCommand command) {
        if (!m_active || !m_recording || !m_source.Seats.Contains(value: command.Principal) || !m_bodies.Contains(item: command.EntityIndex) ||
            (command is not (WorldCommand.SnapPose or WorldCommand.PressChannel or WorldCommand.EnqueueSegment or WorldCommand.Stop or WorldCommand.RigidImpulse))) { return; }
        m_commands.Add(item: (m_cursor, command));
        HashCommand(command: command, tick: m_cursor);
    }
    /// <summary>Records an admitted intent addressed to a body this scope owns, preserving the actor and held image.</summary>
    public void Observe(in IntentSubmission intent) {
        if (!m_active || !m_recording || !m_source.Seats.Contains(value: intent.Principal) || !m_bodies.Contains(item: intent.EntityIndex)) { return; }
        var relative = intent with { Tick = 0 };

        m_intents.Add(item: (m_cursor, relative)); HashIntent(intent: relative, tick: m_cursor);
    }
    /// <summary>Closes a real simulation tick and compares its scope with the corresponding recorded live tick.</summary>
    public void AfterTick() {
        if (!m_active) { return; }
        var actual = Hash();

        if (m_recording) { m_hashes.Add(item: actual); m_recordHash.Add(value: actual); } else if ((actual != m_hashes[m_cursor]) && !m_diverged) {
            m_diverged = true;
            m_server.Output.Narrate("replay.scope", $"[replay.scope: {m_source.Name} MISMATCH tick {(m_cursor + 1)} expected {m_hashes[m_cursor]:x16} actual {actual:x16}]");
        }
        Write(key: "cursor", value: ++m_cursor);
        if (m_cursor < m_source.DurationTicks) { return; }
        m_active = false;
        StopInput();
        Write(key: "status", value: (m_recording ? 3 : (m_diverged ? -1 : 4)));
        Write(key: "completed", value: (Read(key: "completed") + 1));
        m_server.Output.Narrate("replay.scope", $"[replay.scope: {m_source.Name} {(m_recording ? "RECORDED" : (m_diverged ? "MISMATCH" : "MATCH"))} {m_cursor} ticks scope {actual:x16} inputs {m_inputs.Count}]");
        m_recording = false;
    }
    /// <summary>The read-back, including both the live scope hash and the original live recording's tail.</summary>
    public string Describe() => $"{m_source.Name} status {Read(key: "status")} tick {m_cursor}/{m_source.DurationTicks} inputs {m_inputs.Count} start {m_startHash:x16} expected {((m_hashes.Count == 0) ? 0 : m_hashes[^1]):x16} actual {Hash():x16}";

    private void HashInput(int tick, WorldMutation mutation) {
        if (!WorldSubmissionCodec.TryEncodeCommittedMutation(bytes: out var bytes, failure: out var failure, mutation: mutation)) { throw new InvalidOperationException(message: failure.ToString()); }
        m_inputHash.Add(value: tick); m_inputHash.Add(values: bytes);
    }
    private void HashCommand(int tick, WorldCommand command) {
        if (!WorldSubmissionCodec.TryEncodeCommand(bytes: out var bytes, command: command, failure: out var failure)) { throw new InvalidOperationException(message: failure.ToString()); }
        m_commandHash.Add(value: tick); m_commandHash.Add(values: bytes);
    }
    private void HashIntent(int tick, in IntentSubmission intent) {
        var writer = new WireWriter();

        if (!WorldWireCodec.TryWriteIntentSubmission(submission: in intent, writer: writer)) { throw new InvalidOperationException(message: "Scope intent could not encode."); }
        m_intentHash.Add(value: tick); m_intentHash.Add(values: writer.WrittenSpan);
    }

    /// <summary>Folds the replay continuation without serializing the growing tape each tick.</summary>
    public void AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(value: Fnv1aHash.Compute(values: Name.AsSpan())); hash.Add(value: m_startHash);
        hash.Add(value: m_startImageHash);
        hash.Add(value: m_cursor); hash.Add(value: m_inputCursor); hash.Add(value: (m_recording ? 1 : 0)); hash.Add(value: (m_active ? 1 : 0)); hash.Add(value: (m_diverged ? 1 : 0));
        hash.Add(value: m_inputHash.Value); hash.Add(value: m_recordHash.Value);
        hash.Add(value: m_commandHash.Value); hash.Add(value: m_intentHash.Value);
    }
    /// <summary>Captures the derived tape for a whole-session replay or checkpoint.</summary>
    public WorldScopedReplayCheckpoint Checkpoint() => WorldAuthorityCheckpointCodec.CopyScopedReplay(scope: new(Name, m_start, m_startBodies, m_startHash,
        m_inputs.ToArray(), m_hashes.ToArray(), m_cursor, m_inputCursor, m_recording, m_active, m_diverged, m_commands.ToArray(), m_intents.ToArray(),
        m_startRuleLatch, m_startInteractionLatch, m_startGroups));
    /// <summary>Restores the tape beside an already restored arena and population.</summary>
    public void RestoreCheckpoint(WorldScopedReplayCheckpoint checkpoint) {
        checkpoint = WorldAuthorityCheckpointCodec.CopyScopedReplay(scope: checkpoint);
        ValidateCheckpoint(checkpoint: checkpoint);
        m_start = checkpoint.Start; m_startBodies = checkpoint.Bodies.ToArray(); m_startHash = checkpoint.StartHash;
        m_startRuleLatch = checkpoint.RuleGateHeld.ToArray(); m_startInteractionLatch = checkpoint.InteractionGateHeld.ToArray(); m_startGroups = checkpoint.RuleGroups.ToArray();
        m_startImageHash = ((checkpoint.Start is null) ? 0 : WorldAuthorityCheckpointCodec.HashScopedReplayStart(scope: checkpoint));
        m_inputs.Clear(); m_inputs.AddRange(collection: checkpoint.Inputs); m_hashes.Clear(); m_hashes.AddRange(collection: checkpoint.Hashes);
        m_commands.Clear(); m_commands.AddRange(collection: checkpoint.Commands); m_intents.Clear(); m_intents.AddRange(collection: checkpoint.Intents);
        m_cursor = checkpoint.Cursor; m_inputCursor = checkpoint.InputCursor; m_recording = checkpoint.Recording;
        m_active = checkpoint.Active; m_diverged = checkpoint.Diverged;
        m_inputHash = Fnv1aHash.Create(); m_recordHash = Fnv1aHash.Create();
        m_commandHash = Fnv1aHash.Create(); m_intentHash = Fnv1aHash.Create();
        foreach (var (tick, mutation) in m_inputs) { HashInput(mutation: mutation, tick: tick); }
        foreach (var hash in m_hashes) { m_recordHash.Add(value: hash); }
        foreach (var (tick, command) in m_commands) { HashCommand(command: command, tick: tick); }
        foreach (var (tick, intent) in m_intents) { HashIntent(intent: in intent, tick: tick); }
    }
}
