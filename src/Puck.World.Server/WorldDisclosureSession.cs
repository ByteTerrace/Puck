using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// One unembodied session the noninterference harness drives: admitted through the world's own <c>admission</c> rows
/// for a viewer observing from a named authority, it records into a <see cref="WorldDisclosureTranscript"/> everything
/// the session receives, as it arrives: each delivery its sink is handed (a snapshot as the federation wire encodes
/// it, a definition or state delivery as its version, stamp and canonical document), each submission's completion (an
/// outcome as the peer wire's Completion lane encodes it), each query answer, and the stream's attach and end with the
/// end's cause. Its actions submit under the session's own principal through the server's one ordered domain.
/// </summary>
/// <remarks>Actions run on the host pump between ticks. Operation ids come from a seeded
/// <see cref="WorldOperationIds"/>, so both worlds of a pair submit identical envelopes.</remarks>
public sealed class WorldDisclosureSession : IClientSink, IDisposable {
    /// <summary>The door a snapshot delivery is recorded under.</summary>
    public const string SnapshotDoor = "deliver.snapshot";
    /// <summary>The prefix of the doors a mutation's typed completion is recorded under (<c>outcome.set</c>).</summary>
    public const string OutcomeDoor = "outcome.";
    /// <summary>The door a stream's attach and end are recorded under.</summary>
    public const string LifetimeDoor = "lifetime";

    /// <summary>The session actions the harness draws, each in the argument forms the fuzzer fills: queries, cell
    /// writes, draws, transforms, phase-guarded transforms, and writes guarded by a cell expectation or a spatial read
    /// (raw bounds, in metres) whose fingerprint never matches.</summary>
    public static readonly IReadOnlyList<WorldDisclosureVerb> Actions = [
        WorldDisclosureVerb.Spelled(forms: "[<row>]", name: "query.state"),
        WorldDisclosureVerb.Spelled(forms: "<index>", name: "query.where"),
        WorldDisclosureVerb.Spelled(forms: "", name: "query.properties"),
        WorldDisclosureVerb.Spelled(forms: "<row> <key> <value>", name: "set"),
        WorldDisclosureVerb.Spelled(forms: "<row> <key> <value>", name: "add"),
        WorldDisclosureVerb.Spelled(forms: "<row> <key>", name: "remove"),
        WorldDisclosureVerb.Spelled(forms: "<row> [<key>]", name: "generate"),
        WorldDisclosureVerb.Spelled(forms: "<transform-json>", name: "transform"),
        WorldDisclosureVerb.Spelled(forms: "<phase-row> <sequence> <transform-json>", name: "act"),
        WorldDisclosureVerb.Spelled(forms: "<row> <key> <value> <row> <key> <value>", name: "guarded"),
        WorldDisclosureVerb.Spelled(forms: "<row> <key> <value> <x> <z> <x> <z>", name: "spatial"),
    ];

    private readonly WorldServer m_server;
    private readonly WorldDisclosureTranscript m_transcript;
    private readonly WorldOperationIds m_operations;
    private readonly string m_label;
    private WorldSessionObservation? m_observation;
    private long m_sequence;

    private WorldDisclosureSession(WorldServer server, WorldDisclosureTranscript transcript, string label, ulong seed) {
        m_server = server;
        m_transcript = transcript;
        m_label = label;
        m_operations = WorldOperationIds.Seeded(seed: seed);
    }

    /// <summary>Gets the session's principal, or <see langword="null"/> when the world refused it.</summary>
    public Principal? Principal => m_observation?.Session;
    /// <summary>Gets the session's label in the transcript (<c>session:&lt;authority&gt;</c>).</summary>
    public string Label => m_label;

    /// <summary>Admits a session observing <paramref name="server"/> from <paramref name="sourceAuthority"/> and
    /// records its stream's start (its tier) on the <see cref="LifetimeDoor"/>. A fixture observing a session claims
    /// its doors, so a world that refuses it leaves the transcript invalid.</summary>
    /// <param name="server">The world observed.</param>
    /// <param name="sourceAuthority">The authority the viewer observes from, which the world's admission rows match.</param>
    /// <param name="transcript">The transcript the session records into.</param>
    /// <param name="seed">The harness seed operation ids derive from.</param>
    /// <returns>The session; a refused one records its refusal and drives nothing.</returns>
    public static WorldDisclosureSession Observe(WorldServer server, string sourceAuthority, WorldDisclosureTranscript transcript, ulong seed) {
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: transcript);
        var session = new WorldDisclosureSession(label: $"session:{sourceAuthority}", seed: seed, server: server, transcript: transcript);

        session.m_observation = server.TryObserveAsSession(refusal: out var refusal, sink: session, sourceAuthority: sourceAuthority);
        if (session.m_observation is { } observation) {
            observation.EndedBecause += session.Ended;
            transcript.NoteSetup(note: $"{session.m_label} admitted as {observation.Session.Describe()} at tier {observation.Tier}");
            session.Lifetime(text: $"attached tier={observation.Tier}");
        } else {
            transcript.Invalidate(reason: $"{session.m_label} was not admitted, so its doors are not observed: {refusal}");
            session.Lifetime(text: $"refused {refusal}");
        }
        return session;
    }

    /// <summary>Performs one drawn action under the session's principal; what it answers is recorded as it arrives.</summary>
    /// <param name="words">The action: one of <see cref="Actions"/> and its words.</param>
    public void Act(IReadOnlyList<string> words) {
        ArgumentNullException.ThrowIfNull(argument: words);
        if (words.Count == 0) { return; }
        if (m_observation is not { Ended: false } observation) {
            m_transcript.RecordText(door: "action", principal: m_label, text: $"{string.Join(separator: ' ', values: words)}\nno live session");
            return;
        }
        var verb = words[0];
        var session = observation.Session;

        try {
            switch (verb) {
                case "query.state":
                    Query(door: verb, query: new WorldQuery.StateObservations(Row: Word(words: words, index: 1)));
                    return;
                case "query.where":
                    Query(door: verb, query: new WorldQuery.PlayerWhere(Index: (int)Number(words: words, index: 1)));
                    return;
                case "query.properties":
                    Query(door: verb, query: new WorldQuery.Properties());
                    return;
                case "set" or "add":
                    Submit(door: verb, mutation: Write(index: 1, kind: ((verb == "add") ? WorldDocumentWriteKind.Add : WorldDocumentWriteKind.Set), session: session, words: words));
                    return;
                case "remove":
                    Submit(door: verb, mutation: new WorldMutation.RemoveStateCell(Key: Word(words: words, index: 2) ?? "k", Principal: session, Row: Word(words: words, index: 1) ?? "r"));
                    return;
                case "generate":
                    Submit(door: verb, mutation: new WorldMutation.Generate(Keys: ((Word(words: words, index: 2) is { } key) ? new[] { key } : null), Principal: session, Row: Word(words: words, index: 1) ?? "r"));
                    return;
                case "transform":
                    Submit(door: verb, mutation: new WorldMutation.TransformState(Principal: session, Transform: Transform(json: Rest(words: words, index: 1))));
                    return;
                case "act":
                    Submit(door: verb, mutation: new WorldMutation.TransformState(Guard: new PhaseGuard(Row: Word(words: words, index: 1) ?? "r", Sequence: Number(words: words, index: 2)),
                        Principal: session, Transform: Transform(json: Rest(words: words, index: 3))));
                    return;
                case "guarded":
                    Submit(door: verb, mutation: new WorldMutation.Batch(ExpectedCells: [new WorldStateExpectation(Key: Word(words: words, index: 5), Row: Word(words: words, index: 4) ?? "r",
                        Value: Number(words: words, index: 6))], Mutations: [Write(index: 1, kind: WorldDocumentWriteKind.Set, session: session, words: words)], Principal: session));
                    return;
                case "spatial": {
                        // The two corners in either order bound one region.
                        var (x0, z0, x1, z1) = (Number(words: words, index: 4) << 16, Number(words: words, index: 5) << 16, Number(words: words, index: 6) << 16, Number(words: words, index: 7) << 16);

                        Submit(door: verb, mutation: new WorldMutation.Batch(ExpectedSpatialReads: [new WorldSpatialReadDependency(Fingerprint: new string(c: '0', count: 64),
                            MaxXRaw: Math.Max(val1: x0, val2: x1), MaxZRaw: Math.Max(val1: z0, val2: z1), MinXRaw: Math.Min(val1: x0, val2: x1), MinZRaw: Math.Min(val1: z0, val2: z1))],
                            Mutations: [Write(index: 1, kind: WorldDocumentWriteKind.Set, session: session, words: words)], Principal: session));
                        return;
                    }
                default:
                    m_transcript.RecordText(door: "action", principal: m_label, text: $"{string.Join(separator: ' ', values: words)}\nnot a session action");
                    return;
            }
        } catch (Exception exception) when (exception is JsonException or NotSupportedException) {
            m_transcript.RecordText(door: "action", principal: m_label, text: $"{string.Join(separator: ' ', values: words)}\nnot a transform: {exception.Message}");
        }
    }
    /// <summary>Ends the session. A stream still live records so on the <see cref="LifetimeDoor"/>; the harness's own
    /// end is not one the world caused, so it records no cause.</summary>
    public void Dispose() {
        if (m_observation is { } observation) {
            observation.EndedBecause -= Ended;
            if (!observation.Ended) { Lifetime(text: "live at the run's end"); }
            observation.Dispose();
            m_observation = null;
        }
    }

    /// <inheritdoc/>
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        byte[] encoded;

        try {
            encoded = WorldFederationCodec.EncodeSnapshot(snapshot: in snapshot);
        } catch (ArgumentException exception) {
            m_transcript.RecordText(door: SnapshotDoor, principal: m_label, text: $"unencodable {exception.Message}");
            return;
        }
        m_transcript.RecordBytes(door: SnapshotDoor, payload: encoded, principal: m_label);
    }
    /// <inheritdoc/>
    public void DeliverAnswer(in QueryAnswer answer) => m_transcript.RecordText(door: "deliver.answer", principal: m_label, text: Answer(answer: answer));
    /// <inheritdoc/>
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) => m_transcript.RecordText(door: "deliver.definition", principal: m_label,
        text: $"version={m_transcript.Spell(version: version)}\n{Document(definition: definition)}");
    /// <inheritdoc/>
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) => m_transcript.RecordText(door: "deliver.state", principal: m_label,
        text: string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"version={m_transcript.Spell(version: version)} tick={stamp.Tick} engineTick={stamp.EngineTick} moved=[{string.Join(separator: ",", values: stamp.MovedRows.ToArray())}] everything={stamp.Everything} arena={stamp.ArenaValues}\n{Document(definition: definition)}"));
    /// <inheritdoc/>
    public void DeliverComposition(WorldComposition composition) {
        if (WorldSubmissionCodec.TryEncodeComposition(bytes: out var bytes, composition: composition, failure: out var failure)) {
            m_transcript.RecordBytes(door: "deliver.composition", payload: bytes, principal: m_label);
        } else {
            m_transcript.RecordText(door: "deliver.composition", principal: m_label, text: $"unencodable {failure}");
        }
    }
    /// <inheritdoc/>
    public void DeliverSessionLever(WorldSessionLever lever) {
        if (WorldSubmissionCodec.TryEncodeLever(bytes: out var bytes, failure: out var failure, lever: lever)) {
            m_transcript.RecordBytes(door: "deliver.lever", payload: bytes, principal: m_label);
        } else {
            m_transcript.RecordText(door: "deliver.lever", principal: m_label, text: $"unencodable {failure}");
        }
    }

    private void Ended(string reason) => Lifetime(text: $"ended {reason}");
    private void Lifetime(string text) => m_transcript.RecordText(door: LifetimeDoor, principal: m_label, text: text);
    private static string Answer(in QueryAnswer answer) => $"refused={answer.Refused}\n{answer.Text}";
    private static string Document(WorldDefinition definition) => Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.SerializeCompact(definition: definition));
    // The next envelope under the session's principal: its sequence, and an operation id both worlds of a pair agree on.
    private SubmissionEnvelope Envelope(WorldSubmissionPayload payload) {
        var sequence = ++m_sequence;
        var session = m_observation!.Session;

        return new SubmissionEnvelope(ConnectionId: SubmissionEnvelope.LocalConnectionId, CorrelationId: sequence, OperationId: m_operations.Next(principal: session),
            Payload: payload, Principal: session, Sequence: sequence, SessionGeneration: 0);
    }
    private void Query(string door, WorldQuery query) => m_server.Submit(completion: result => m_transcript.RecordText(door: door, principal: m_label,
        text: ((result is WorldSubmissionResult.Query { Answer: var answer }) ? Answer(answer: answer) : Outcome(result: result))), envelope: Envelope(payload: new WorldSubmissionPayload.Query(Value: query)));
    private void Submit(string door, WorldMutation mutation) => m_server.Submit(completion: Complete(door: (OutcomeDoor + door)),
        envelope: Envelope(payload: new WorldSubmissionPayload.Mutation(Value: mutation)));
    private Action<WorldSubmissionResult> Complete(string door) => result => {
        var (kind, body) = WorldPeerWireFormat.EncodeResult(result: ((result is WorldSubmissionResult.Mutation { Outcome: var outcome })
            ? new WorldSubmissionResult.Mutation(Outcome: outcome with { Version = m_transcript.Nonce(version: outcome.Version) })
            : result));
        var payload = new byte[(body.Length + 1)];

        payload[0] = (byte)kind;
        body.CopyTo(array: payload, index: 1);
        m_transcript.RecordBytes(door: door, payload: payload, principal: m_label);
    };
    private string Outcome(WorldSubmissionResult result) => result switch {
        WorldSubmissionResult.Refusal refusal => $"refusal code={refusal.Code}\n{refusal.Detail}",
        _ => result.ToString(),
    };
    private static WorldMutation.UpsertStateCell Write(IReadOnlyList<string> words, int index, WorldDocumentWriteKind kind, Principal session) => new(
        Key: (Word(words: words, index: (index + 1)) ?? "k"), Kind: kind, Principal: session, RawToken: (Word(words: words, index: (index + 2)) ?? "0"),
        Row: (Word(words: words, index: index) ?? "r"), Value: 0L);
    private static StateTransform Transform(string json) => (JsonSerializer.Deserialize(json: json, jsonTypeInfo: WorldJsonContext.Default.StateTransform)
        ?? throw new JsonException(message: "a state transform is an object"));
    private static string? Word(IReadOnlyList<string> words, int index) => ((index < words.Count) ? words[index] : null);
    private static string Rest(IReadOnlyList<string> words, int index) => string.Join(separator: ' ', values: words.Skip(count: index));
    private static long Number(IReadOnlyList<string> words, int index) => (((index < words.Count) && long.TryParse(s: words[index], provider: CultureInfo.InvariantCulture, result: out var value)) ? value : 0L);
}
