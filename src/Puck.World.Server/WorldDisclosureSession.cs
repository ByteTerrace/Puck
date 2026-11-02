using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// One unembodied session the noninterference harness drives: admitted through the world's own <c>admission</c> rows
/// for a viewer observing from a named authority, its sink records every delivery (the primer and each definition,
/// state and snapshot delivery, with its version, count and size per tick) into a <see cref="WorldDisclosureTranscript"/>,
/// and its actions (queries and state mutations under the session's own principal) record their answers and typed
/// outcomes beside them. The session's tier is the world's to decide, so one fixture observes a replica and a
/// presentation session by naming the two authorities its admission rows grant those tiers to.
/// </summary>
/// <remarks>Actions run on the host pump between ticks. A submission's typed outcome arrives when its tick applies and
/// is recorded at that tick. Operation ids are derived from the harness seed and the action's ordinal, never from a
/// random source, so both worlds of a pair submit identical envelopes.</remarks>
public sealed class WorldDisclosureSession : IClientSink, IDisposable {
    /// <summary>The session actions the harness draws: <c>query.state [row]</c>, <c>query.where &lt;body&gt;</c>,
    /// <c>query.properties</c>, <c>set|add &lt;row&gt; &lt;key&gt; &lt;value&gt;</c> and <c>remove &lt;row&gt; &lt;key&gt;</c>.</summary>
    public static readonly IReadOnlyList<string> Actions = ["query.state", "query.where", "query.properties", "set", "add", "remove"];

    private readonly WorldServer m_server;
    private readonly WorldDisclosureTranscript m_transcript;
    private readonly string m_label;
    private readonly ulong m_seed;
    private WorldSessionObservation? m_observation;
    private long m_sequence;

    private WorldDisclosureSession(WorldServer server, WorldDisclosureTranscript transcript, string label, ulong seed) {
        m_server = server;
        m_transcript = transcript;
        m_label = label;
        m_seed = seed;
    }

    /// <summary>Gets the session's principal, or <see langword="null"/> when the world refused it.</summary>
    public Principal? Principal => m_observation?.Session;

    /// <summary>Admits a session observing <paramref name="server"/> from <paramref name="sourceAuthority"/> and
    /// records its stream's start (its tier, or the world's refusal) as the <c>lifetime</c> door.</summary>
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
        session.Record(door: "lifetime", text: ((session.m_observation is { } observation)
            ? $"attached {observation.Session.Describe()} tier={observation.Tier}"
            : $"refused {refusal}"));
        return session;
    }

    /// <summary>Performs one drawn action under the session's principal and records what it answered.</summary>
    /// <param name="action">The action text: one of <see cref="Actions"/> and its words.</param>
    public void Act(string action) {
        ArgumentNullException.ThrowIfNull(argument: action);
        if (m_observation is not { Ended: false } observation) {
            Record(door: "action", text: $"{action} -> no live session");
            return;
        }
        var words = action.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);
        var verb = words[0];
        var session = observation.Session;

        switch (verb) {
            case "query.state":
                Answer(door: verb, answer: m_server.AnswerSubmittedQuery(principal: session, query: new WorldQuery.StateObservations(Row: Word(words: words, index: 1))));
                return;
            case "query.where":
                Answer(door: verb, answer: m_server.AnswerSubmittedQuery(principal: session, query: new WorldQuery.PlayerWhere(Index: Number(words: words, index: 1))));
                return;
            case "query.properties":
                Answer(door: verb, answer: m_server.AnswerSubmittedQuery(principal: session, query: new WorldQuery.Properties()));
                return;
            case "set" or "add":
                Submit(door: $"outcome.{verb}", mutation: new WorldMutation.UpsertStateCell(Key: (Word(words: words, index: 2) ?? "k"),
                    Kind: ((verb == "add") ? WorldDocumentWriteKind.Add : WorldDocumentWriteKind.Set), Principal: session,
                    Row: (Word(words: words, index: 1) ?? "r"), Value: ((long)Number(words: words, index: 3) << 16)));
                return;
            case "remove":
                Submit(door: "outcome.remove", mutation: new WorldMutation.RemoveStateCell(Key: (Word(words: words, index: 2) ?? "k"),
                    Principal: session, Row: (Word(words: words, index: 1) ?? "r")));
                return;
            default:
                Record(door: "action", text: $"{action} -> not a session action");
                return;
        }
    }
    /// <summary>Ends the session and records the stream's end as the <c>lifetime</c> door.</summary>
    public void Dispose() {
        if (m_observation is { } observation) {
            Record(door: "lifetime", text: (observation.Ended ? "ended before the run did" : "live at the run's end"));
            observation.Dispose();
            m_observation = null;
        }
    }

    /// <inheritdoc/>
    public void DeliverSnapshot(in WorldSnapshot snapshot) => Record(door: "deliver.snapshot", text: Encode(snapshot: snapshot));
    /// <inheritdoc/>
    public void DeliverAnswer(in QueryAnswer answer) => Record(door: "deliver.answer", text: $"refused={answer.Refused} {answer.Text}");
    /// <inheritdoc/>
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) => Record(door: "deliver.definition",
        text: $"version={Version(version: version)} {m_transcript.Blob(content: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.SerializeCompact(definition: definition)))}");
    /// <inheritdoc/>
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) => Record(door: "deliver.state",
        text: string.Create(provider: CultureInfo.InvariantCulture, handler: $"version={Version(version: version)} tick={stamp.Tick} engineTick={stamp.EngineTick} moved=[{string.Join(separator: ",", values: stamp.MovedRows.ToArray())}] everything={stamp.Everything} arena={stamp.ArenaValues} placements={stamp.PlacementProjection} {m_transcript.Blob(content: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.SerializeCompact(definition: definition)))}"));
    /// <inheritdoc/>
    public void DeliverComposition(WorldComposition composition) => Record(door: "deliver.composition", text: composition.ToString());
    /// <inheritdoc/>
    public void DeliverSessionLever(WorldSessionLever lever) => Record(door: "deliver.lever", text: lever.ToString());

    private ulong Tick => ((m_server.NextInputTick > 0UL) ? (m_server.NextInputTick - 1UL) : 0UL);

    private void Record(string door, string text) => m_transcript.Record(bytes: text, door: door, principal: m_label, tick: Tick);
    private void Answer(string door, QueryAnswer answer) => Record(door: door, text: $"refused={answer.Refused} {answer.Text}");
    private void Submit(string door, WorldMutation mutation) {
        var sequence = ++m_sequence;
        Span<byte> material = stackalloc byte[16];

        BinaryPrimitives.WriteUInt64LittleEndian(destination: material, value: m_seed);
        BinaryPrimitives.WriteInt64LittleEndian(destination: material[8..], value: sequence);
        var operation = new Guid(b: SHA256.HashData(source: material).AsSpan(start: 0, length: 16));

        m_server.Submit(completion: result => Record(door: door, text: Outcome(result: result)), envelope: new SubmissionEnvelope(
            ConnectionId: SubmissionEnvelope.LocalConnectionId, CorrelationId: sequence, OperationId: operation,
            Payload: new WorldSubmissionPayload.Mutation(Value: mutation), Principal: m_observation!.Session, Sequence: sequence, SessionGeneration: 0));
    }
    private string Outcome(WorldSubmissionResult result) => result switch {
        WorldSubmissionResult.Mutation { Outcome: var outcome } => string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"decision={outcome.Decision} code={outcome.Code} version={Version(version: outcome.Version)} revision={outcome.AffectedGroupRevision} persistence={outcome.PersistenceStatus} digest={outcome.PayloadDigest} detail={outcome.Detail}"),
        WorldSubmissionResult.Refusal refusal => $"refusal code={refusal.Code} detail={refusal.Detail}",
        _ => result.ToString(),
    };
    private string Version(WorldDocumentVersion version) => string.Create(provider: CultureInfo.InvariantCulture,
        handler: $"{m_transcript.Nonce(value: version.Activation)}.{version.Sequence}");
    private static string? Word(string[] words, int index) => ((index < words.Length) ? words[index] : null);
    private static int Number(string[] words, int index) => (((index < words.Length) && int.TryParse(s: words[index], provider: CultureInfo.InvariantCulture, result: out var value)) ? value : 0);
    private static string Encode(in WorldSnapshot snapshot) {
        var text = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;

        text.Append(provider: invariant, handler: $"tick={snapshot.Tick} revision={snapshot.Revision} step={snapshot.StepTicks} engineTick={snapshot.EngineTick} authority={snapshot.Authority} full={snapshot.FieldsFull}");
        foreach (var entry in snapshot.Entries.Span) {
            text.Append(provider: invariant, handler: $" | body {entry.Index}.{entry.Generation} active={entry.Active} at=({entry.Position.X:R},{entry.Position.Y:R},{entry.Position.Z:R}) q=({entry.Orientation.X:R},{entry.Orientation.Y:R},{entry.Orientation.Z:R},{entry.Orientation.W:R}) heading={entry.Heading:R} kit={entry.Kit} look={entry.Look} rig={entry.CatalogRig} facts={entry.Facts} continuity={entry.Continuity} placement={entry.PlacementId} state=[{string.Join(separator: ",", values: entry.State.ToArray())}]");
        }
        foreach (var cell in snapshot.FieldCells.Span) {
            text.Append(provider: invariant, handler: $" | cell row={cell.Row} cell={cell.Cell} field={cell.Field} raw={cell.Raw} region={cell.Region} name={cell.FieldName} image={cell.Image.Key}={cell.Image.Value} {cell.Image.Behavior} {cell.Image.Clock}");
        }
        text.Append(provider: invariant, handler: $" | inputs {snapshot.Inputs.Length}");
        return text.ToString();
    }
}
