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
    private readonly HashSet<int> m_rows;

    private readonly List<(int Tick, WorldMutation Mutation)> m_inputs = [];
    private readonly List<ulong> m_hashes = [];
    private readonly List<(int Tick, WorldCommand Command)> m_commands = [];
    private readonly List<(int Tick, IntentSubmission Intent)> m_intents = [];

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

    private readonly int[] m_orderedRows;

    /// <summary>Gets the authored scope identity.</summary>
    public string Name => m_source.Name;

    /// <summary>Binds an authored scope to its live arena. Admission validates row names and ownership.</summary>
    public WorldScopedReplayRuntime(WorldServer server, WorldScopedReplay source) {
        m_server = server;
        m_source = source;
        m_bodies = source.Bodies.ToHashSet();
        m_rows = source.Rows.Select(selector: name => Row(name: name)).ToHashSet();
        m_orderedRows = m_rows.Order().ToArray();
    }

    private int Row(string name) => (m_server.Arena.Catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: name)
        ? handle.Ordinal : throw new InvalidOperationException(message: $"No scope row '{name}'."));
    private CellKey Key(string name) => m_server.Arena.Keys.Intern(name: CellName.Parse(candidate: name));
    private long Read(string key) => (m_server.Arena.TryRead(Row(name: m_source.Control), Key(name: key), out var value) ? value.Raw : 0);
    private void Write(string key, long value) {
        if (!m_server.Arena.TryWrite(Row(name: m_source.Control), Key(name: key), value, StateWriteKind.Set, out var reason)) {
            throw new InvalidOperationException(message: reason);
        }
    }

    /// <summary>Creates a capture (4), drive or derive (1), cancel and restore (2), or restore (3) request.
    /// External callers submit it through their recorded transport door.</summary>
    public WorldMutation CreateRequest(int request, Principal principal) =>
        new WorldMutation.UpsertStateCell(principal, m_source.Control, "request", request, WorldDocumentWriteKind.Set);
    /// <summary>Reads the scope's own live values, independent of unrelated arena keys and bodies.</summary>
    public ulong Hash() {
        var hash = Fnv1aHash.Create();

        foreach (var row in m_orderedRows) { m_server.Arena.AddRowTo(hash: ref hash, rowOrdinal: row); }
        hash.Add(value: WorldReplaySnapshot.HashState(m_server.Population, m_bodies));
        m_server.Population.AppendContinuationHash(bodies: m_bodies, hash: ref hash);
        AppendRuleHash(hash: ref hash);
        return hash.Value;
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
            if ((m_server.Body(index: image.Index) is null) || (m_server.Population.Generation(index: image.Index) != image.Generation)) { throw new InvalidOperationException(message: "A scoped body was replaced; capture again."); }
        }
        m_server.Arena.ValidateScope(image: m_start);
        m_server.Arena.RestoreScope(image: m_start);
        RestoreRules();
        foreach (var image in m_startBodies) {
            var body = m_server.Body(index: image.Index)!;

            body.Pose(image.Position, image.Yaw, FixedQ4816.Zero, FixedQ4816.Zero);
            body.ApplyTransferState(state: image.DynamicState);
            body.ApplyIntegrationResidue(image.Residue, m_server.Population.ContactFieldVersion);
        }
        if (Hash() != m_startHash) { throw new InvalidOperationException(message: "Scope starting hash MISMATCH after restore."); }
    }

    /// <summary>Runs at the input boundary, before the normal mutation drain.</summary>
    public void BeforeTick() {
        try { BeforeTickCore(); } catch (InvalidOperationException exception) {
            m_active = false;
            Write(key: "status", value: -1);
            m_server.Output.Narrate("replay.scope", $"[replay.scope: {Name} refused — {exception.Message}]");
        }
    }

    private void BeforeTickCore() {
        var request = Read(key: "request");

        if (request != 0) {
            Write(key: "request", value: 0);
            if (request == 4) {
                if (m_start is null) { Capture(); }
            } else if (request is 2 or 3) {
                m_active = false;
                if (m_recording) {
                    m_inputs.Clear(); m_hashes.Clear(); m_commands.Clear(); m_intents.Clear();
                    m_recordHash = Fnv1aHash.Create(); m_inputHash = Fnv1aHash.Create();
                    m_commandHash = Fnv1aHash.Create(); m_intentHash = Fnv1aHash.Create();
                }
                m_recording = false;
                m_cursor = 0; m_inputCursor = 0; m_diverged = false;
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
                m_server.EnqueueMutation(new WorldMutation.Batch(input.Principal, input.Cells.Select(selector: cell =>
                    ((WorldMutation)new WorldMutation.UpsertStateCell(input.Principal, cell.Row, cell.Key, cell.Value, WorldDocumentWriteKind.Set))).ToArray()));
            }
        } else {
            while ((m_inputCursor < m_inputs.Count) && (m_inputs[m_inputCursor].Tick == m_cursor)) {
                m_server.EnqueueMutation(m_inputs[m_inputCursor++].Mutation);
            }
            foreach (var input in m_commands) { if (input.Tick == m_cursor) { m_server.ApplyCommand(input.Command); } }
            foreach (var input in m_intents) {
                if (input.Tick == m_cursor) { m_server.EnqueueIntent(submission: input.Intent with { Tick = m_server.NextInputTick }); }
            }
        }
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
