using Puck.Commands;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>
/// Laws for <see cref="WorldDeferredVerbEchoes"/>, the console's registered lines: a line registers when a console link
/// mints its correlation, keyed by row, and only a registered line's refusal counts, once. A rebuild or undo verb's
/// verdict prints as its own line; an evicted line counts nothing until its verdict arrives, which counts by its real
/// outcome; a line forgotten past the memory bound counts once as an unknown outcome; a codec refusal on a console
/// link counts once. Echoes no console line registered (another row's, a remote peer's, correlation 0, a grant-table
/// replay of a buffered line, the bare transport's) answer nothing.
/// </summary>
public sealed class DeferredVerbEchoLawTests {
    private const string Row = "row";

    private static List<WorldDeferredVerbAnswer> Answers(WorldDeferredVerbEchoes echoes) {
        var answers = new List<WorldDeferredVerbAnswer>();

        echoes.Answered += answers.Add;

        return answers;
    }
    private static CommandSettlement Register(WorldDeferredVerbEchoes echoes, long id, string row = Row) {
        var settlement = new CommandSettlement();

        _ = echoes.Register(
            correlationId: id,
            row: row,
            settlement: settlement,
            verb: "world.reset"
        );

        return settlement;
    }
    private static void Echo(WorldDeferredVerbEchoes echoes, long id, bool rejected, string row = Row, bool local = true, bool grantTable = false) => echoes.Answer(
        correlationId: id,
        grantTable: grantTable,
        local: local,
        message: (rejected
            ? "refused"
            : "applied"),
        rejected: rejected,
        row: row
    );
    private static CommandResult? Verdict(CommandSettlement settlement) {
        CommandResult? verdict = null;

        Settled(result: CommandResult.Settling(settlement: settlement), observe: result => verdict = result);

        return verdict;
    }

    [Fact]
    public void ARegisteredVerbAnswersOnceWithItsOwnLineAndCountsOnlyARefusal() {
        var echoes = new WorldDeferredVerbEchoes();
        var answers = Answers(echoes: echoes);
        var applied = Register(echoes: echoes, id: 1L);
        var refused = Register(echoes: echoes, id: 2L);

        Echo(echoes: echoes, id: 1L, rejected: false);
        Echo(echoes: echoes, id: 1L, rejected: true);
        Echo(echoes: echoes, id: 2L, rejected: true);

        Assert.Equal(
            actual: answers,
            expected: [
                new WorldDeferredVerbAnswer(Counts: false, IsError: false, Line: "[world.reset: applied]"),
                new WorldDeferredVerbAnswer(Counts: true, IsError: true, Line: "[world.reset: refused]"),
            ]
        );
        Assert.Equal(actual: Verdict(settlement: applied)!.Value.Output, expected: "[world.reset: applied]");
        Assert.True(condition: Verdict(settlement: refused)!.Value.IsError);
        Assert.Equal(actual: echoes.PendingCount, expected: 0);
    }
    [InlineData("unregistered")]
    [InlineData("zero")]
    [InlineData("remote")]
    [InlineData("another row")]
    [InlineData("grant-table replay")]
    [Theory]
    public void AnEchoNoConsoleLineRegisteredAnswersNothing(string echo) {
        var echoes = new WorldDeferredVerbEchoes();
        var answers = Answers(echoes: echoes);

        _ = Register(echoes: echoes, id: 1L);

        switch (echo) {
            case "unregistered":
                Echo(echoes: echoes, id: 2L, rejected: true);
                break;
            case "zero":
                Echo(echoes: echoes, id: 0L, rejected: true);
                break;
            case "remote":
                Echo(echoes: echoes, id: 1L, local: false, rejected: true);
                break;
            case "another row":
                Echo(echoes: echoes, id: 1L, rejected: true, row: "another");
                break;
            default:
                Echo(echoes: echoes, grantTable: true, id: 1L, rejected: true);
                break;
        }

        Assert.Empty(collection: answers);
        Assert.Equal(actual: echoes.PendingCount, expected: 1);

        Echo(echoes: echoes, id: 1L, rejected: true);

        Assert.Single(collection: answers);
    }
    [Fact]
    public void ARegistrationWithNoVerdictToWaitForIsItsOwnAnswer() {
        var echoes = new WorldDeferredVerbEchoes();
        var settled = new CommandSettlement();

        settled.Settle(result: CommandResult.Error(output: "[world.reset: refused inline]"));

        Assert.Equal(actual: echoes.Register(correlationId: 7L, row: Row, settlement: settled, verb: "world.reset").Output, expected: "[world.reset: refused inline]");
        Assert.StartsWith(actualString: echoes.Register(correlationId: 0L, row: Row, settlement: new CommandSettlement(), verb: "world.reset").Output, expectedStartString: "[world.reset: no local verdict is available");
        Assert.Equal(actual: echoes.PendingCount, expected: 0);
    }
    [Fact]
    public void AnsweredLinesDoNotConsumeTheBound() {
        var echoes = new WorldDeferredVerbEchoes();

        for (var id = 1L; (id <= (3 * WorldDeferredVerbEchoes.Capacity)); id++) {
            _ = Register(echoes: echoes, id: id);
            Echo(echoes: echoes, id: id, rejected: false);
        }

        Assert.Equal(actual: (echoes.PendingCount, echoes.EvictedCount), expected: (0, 0));
    }
    /// <summary>An evicted line releases its session and neither prints nor counts; when its verdict arrives it answers
    /// with its own line and counts by its real outcome, once.</summary>
    [Fact]
    public void AnEvictedLineAnswersAndCountsByItsRealOutcomeWhenItsVerdictArrives() {
        var echoes = new WorldDeferredVerbEchoes();
        var answers = Answers(echoes: echoes);
        var first = Register(echoes: echoes, id: 1L);

        for (var id = 2L; (id <= (WorldDeferredVerbEchoes.Capacity + 2L)); id++) {
            _ = Register(echoes: echoes, id: id);
        }

        Assert.Empty(collection: answers);
        Assert.Equal(actual: (echoes.PendingCount, echoes.EvictedCount), expected: (WorldDeferredVerbEchoes.Capacity, 2));
        Assert.StartsWith(actualString: Verdict(settlement: first)!.Value.Output, expectedStartString: "[world.reset: evicted unanswered");

        Echo(echoes: echoes, id: 1L, rejected: false);
        Echo(echoes: echoes, id: 2L, rejected: true);
        Echo(echoes: echoes, id: 2L, rejected: true);

        Assert.Equal(
            actual: answers,
            expected: [
                new WorldDeferredVerbAnswer(Counts: false, IsError: false, Line: "[world.reset: applied]"),
                new WorldDeferredVerbAnswer(Counts: true, IsError: true, Line: "[world.reset: refused]"),
            ]
        );
        Assert.Equal(actual: echoes.EvictedCount, expected: 0);
    }
    /// <summary>Past the memory bound the oldest evicted line is forgotten: it answers once as an unknown outcome and
    /// counts once, and its verdict arriving later answers nothing, so it is never counted twice.</summary>
    [Fact]
    public void AForgottenLineCountsOnceAndItsLateVerdictAnswersNothing() {
        var echoes = new WorldDeferredVerbEchoes();
        var answers = Answers(echoes: echoes);
        var last = ((WorldDeferredVerbEchoes.Capacity + WorldDeferredVerbEchoes.EvictedMemory) + 1L);

        for (var id = 1L; (id <= last); id++) {
            _ = Register(echoes: echoes, id: id);
        }

        var forgotten = Assert.Single(collection: answers);

        Assert.True(condition: (forgotten.Counts && forgotten.IsError));
        Assert.StartsWith(actualString: forgotten.Line, expectedStartString: "[world.reset: unanswered");

        Echo(echoes: echoes, id: 1L, rejected: true);
        Echo(echoes: echoes, id: 2L, rejected: true);

        Assert.Equal(actual: answers.Count(predicate: static answer => answer.Counts), expected: 2);
        Assert.Equal(actual: answers[^1].Line, expected: "[world.reset: refused]");
    }
    /// <summary>A grant applies inside its submit, so its echo arrives before the correlation returns: a console link
    /// registers the line as it mints it, so a refusal counts once and nothing is left pending; the bare transport
    /// registers nothing.</summary>
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Theory]
    public void ASynchronousRefusalCountsOnceOnlyThroughAConsoleLink(bool console, bool rejected) {
        var echoes = new WorldDeferredVerbEchoes();
        var answers = Answers(echoes: echoes);
        var transport = new LoopbackTransport(server: new EchoingHost(echoes: echoes, rejectGrants: rejected));
        IServerLink link = (console
            ? transport.ForConsole(row: echoes.ForRow(row: Row))
            : transport);

        link.SubmitGrant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Capability: WorldCapability.Mutate,
                Exclusive: false,
                Grantee: Principal.Console,
                Subject: GrantSubject.Section(section: WorldSection.State)
            )
        );

        Assert.Equal(actual: answers.Count(predicate: static answer => answer.Counts), expected: ((console && rejected) ? 1 : 0));
        Assert.All(collection: answers, action: static answer => Assert.Null(@object: answer.Line));
        Assert.Equal(actual: echoes.PendingCount, expected: 0);
    }
    /// <summary>A mutation waits for the next tick boundary: its console line stays pending until its verdict answers
    /// it.</summary>
    [Fact]
    public void ABufferedConsoleLineWaitsForItsVerdict() {
        var echoes = new WorldDeferredVerbEchoes();
        var answers = Answers(echoes: echoes);
        var host = new EchoingHost(echoes: echoes, rejectGrants: false);
        IServerLink link = new LoopbackTransport(server: host).ForConsole(row: echoes.ForRow(row: Row));

        _ = link.Submit(mutation: new WorldMutation.RemoveKit(Principal.Console, "one"));

        Assert.Equal(actual: echoes.PendingCount, expected: 1);
        host.EchoLast(grantTable: false, rejected: true);
        Assert.Equal(actual: (answers.Count(predicate: static answer => answer.Counts), echoes.PendingCount), expected: (1, 0));
    }
    /// <summary>A rebuild replays its document's grants under its own correlation: those grant-table echoes neither
    /// settle nor count the rebuild's line, and the rebuild's own verdict does.</summary>
    [Fact]
    public void ARebuildsGrantReplaysLeaveItsLineForItsOwnVerdict() {
        var echoes = new WorldDeferredVerbEchoes();
        var answers = Answers(echoes: echoes);
        var host = new EchoingHost(echoes: echoes, rejectGrants: false);
        IServerLink link = new LoopbackTransport(server: host).ForConsole(row: echoes.ForRow(row: Row));

        _ = link.SubmitRebuild(new WorldRebuildRequest(WorldRebuildKind.Reset, null, null, false), Principal.Console, echoes, "world.reset");
        host.EchoLast(grantTable: true, rejected: true);

        Assert.Empty(collection: answers);

        host.EchoLast(grantTable: false, rejected: false);

        Assert.Equal(actual: Assert.Single(collection: answers), expected: new WorldDeferredVerbAnswer(Counts: false, IsError: false, Line: "[world.reset: applied]"));
    }
    /// <summary>A codec refusal with no completion prints on stderr as the transport's own answer and counts once on a
    /// console link; through the bare transport it counts nothing.</summary>
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void ACodecRefusalWithNoCompletionCountsOnceOnAConsoleLink(bool console) {
        var echoes = new WorldDeferredVerbEchoes();
        var answers = Answers(echoes: echoes);
        var transport = new LoopbackTransport(server: new EchoingHost(echoes: echoes, rejectGrants: false));
        IServerLink link = (console
            ? transport.ForConsole(row: echoes.ForRow(row: Row))
            : transport);
        var oversized = new string(c: 'x', count: (WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Mutation) + 1));

        Assert.Equal(actual: link.SubmitWorldMutation(mutation: new WorldMutation.RemoveKit(Principal.Console, oversized)), expected: 0L);
        Assert.Equal(
            actual: answers,
            expected: (console
                ? [new WorldDeferredVerbAnswer(Counts: true, IsError: true, Line: null)]
                : [])
        );
        Assert.Equal(actual: echoes.PendingCount, expected: 0);
    }
    [Fact]
    public void TwoWorldsWithTheSameCorrelationKeepTheirOwnMutationVerdicts() {
        var echoes = new WorldDeferredVerbEchoes();
        var firstHost = new CompletionHost();
        var secondHost = new CompletionHost();
        var first = new LoopbackTransport(server: firstHost);
        var second = new LoopbackTransport(server: secondHost);
        Puck.Commands.CommandResult? firstResult = null;
        Puck.Commands.CommandResult? secondResult = null;

        Settled(first.Submit(new WorldMutation.RemoveKit(Principal.Console, "one"), echoes, "first"), result => firstResult = result);
        Settled(second.Submit(new WorldMutation.RemoveKit(Principal.Console, "two"), echoes, "second"), result => secondResult = result);
        Assert.Equal(firstHost.Envelope.CorrelationId, secondHost.Envelope.CorrelationId);
        secondHost.Completion!(new WorldSubmissionResult.Refusal(Code: "second.refused", Detail: "second detail"));
        Assert.Null(value: firstResult);
        Assert.Contains("second detail", secondResult!.Value.Output, StringComparison.Ordinal);
        firstHost.Completion!(new WorldSubmissionResult.Refusal(Code: "first.refused", Detail: "first detail"));
        Assert.Contains("first detail", firstResult!.Value.Output, StringComparison.Ordinal);
    }
    [Fact]
    public void InlineIngressRefusalsAreNotLostBeforeRegistration() {
        var host = new CompletionHost { RefuseInline = true };
        var link = new LoopbackTransport(server: host);
        Puck.Commands.CommandResult? result = null;

        Settled(link.Submit(new WorldMutation.RemoveKit(Principal.Console, "one"), new WorldDeferredVerbEchoes(), "edit"), verdict => result = verdict);
        Assert.True(condition: result!.Value.IsError);
        Assert.Contains("retiring", result.Value.Output, StringComparison.Ordinal);
    }
    [Fact]
    public void CodecRefusalSettlesWithoutEverReachingTheAuthority() {
        var host = new CompletionHost();
        var link = new LoopbackTransport(server: host);
        Puck.Commands.CommandResult? result = null;
        var oversized = new string(c: 'x', count: (WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Mutation) + 1));

        Settled(link.Submit(new WorldMutation.RemoveKit(Principal.Console, oversized), new WorldDeferredVerbEchoes(), "edit"), verdict => result = verdict);
        Assert.True(condition: result!.Value.IsError);
        Assert.Contains("codec_refused", result.Value.Output, StringComparison.Ordinal);
        Assert.Null(@object: host.Completion);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void RebuildAndUndoRetirementRefusalsDoNotWaitForAnEcho(bool undo) {
        var host = new CompletionHost { RefuseInline = true };
        var link = new LoopbackTransport(server: host);
        var echoes = new WorldDeferredVerbEchoes();
        Puck.Commands.CommandResult? result = null;
        var pending = (undo
            ? link.SubmitUndo(1, Principal.Console, echoes, "world.undo")
            : link.SubmitRebuild(new WorldRebuildRequest(WorldRebuildKind.Reset, null, null, false), Principal.Console, echoes, "world.reset"));

        Settled(pending, verdict => result = verdict);
        Assert.True(condition: result!.Value.IsError);
        Assert.Contains("retiring", result.Value.Output, StringComparison.Ordinal);
        Assert.Equal(actual: echoes.PendingCount, expected: 0);
    }
    /// <summary>A verdict that arrived before its line's handler returned is that line's own answer on the stdin
    /// driver's path (a Simulation-routed line, a session that settles nothing, an observer printing results), and
    /// <c>wire.errors</c> counts it once; the table's late-verdict sink never sees it, so it is printed once.</summary>
    [InlineData("world.reset")]
    [InlineData("world.undo")]
    [InlineData("edit")]
    [Theory]
    public void ARefusalSettledBeforeItsLineReturnedIsTheLinesAnswerAndCounted(string verb) {
        var host = new CompletionHost { RefuseInline = true };
        var link = new LoopbackTransport(server: host);
        var echoes = new WorldDeferredVerbEchoes();
        var published = 0;

        echoes.Completed += _ => published++;

        var (answers, registry) = DriveOneLine(handler: () => verb switch {
            "world.reset" => link.SubmitRebuild(new WorldRebuildRequest(WorldRebuildKind.Reset, null, null, false), Principal.Console, echoes, verb),
            "world.undo" => link.SubmitUndo(1, Principal.Console, echoes, verb),
            _ => link.Submit(new WorldMutation.RemoveKit(Principal.Console, "one"), echoes, verb),
        });
        var answer = Assert.Single(collection: answers);

        Assert.True(condition: answer.IsError);
        Assert.StartsWith(actualString: answer.Output, expectedStartString: $"[{verb}: world.retiring retiring]");
        Assert.Equal(actual: published, expected: 0);
        Assert.Equal(actual: registry.Submit(line: "wire.errors").Output, expected: "[wire.errors: 1 rejected]");
    }
    /// <summary>A mutation verdict that arrives after its line returned is published through the table exactly once,
    /// and the line itself answered nothing; the control for the law above.</summary>
    [Fact]
    public void AMutationVerdictArrivingLaterIsPublishedOnceAndNotAnswered() {
        var host = new CompletionHost();
        var link = new LoopbackTransport(server: host);
        var echoes = new WorldDeferredVerbEchoes();
        var published = new List<Puck.Commands.CommandResult>();

        echoes.Completed += published.Add;

        var (answers, _) = DriveOneLine(handler: () => link.Submit(new WorldMutation.RemoveKit(Principal.Console, "one"), echoes, "edit"));

        Assert.Empty(collection: answers);
        Assert.Empty(collection: published);
        host.Completion!(new WorldSubmissionResult.Refusal(Code: "late.refused", Detail: "late detail"));

        var verdict = Assert.Single(collection: published);

        Assert.True(condition: verdict.IsError);
        Assert.Equal(actual: verdict.Output, expected: "[edit: late.refused late detail]");
    }
    /// <summary>A codec refusal names the codec's own reason in the line's answer, not only in the transport's
    /// narration.</summary>
    [Fact]
    public void ACodecRefusalAnswerNamesTheCodecsReason() {
        var link = new LoopbackTransport(server: new CompletionHost());
        var oversized = new string(c: 'x', count: (WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Mutation) + 1));

        var (answers, _) = DriveOneLine(handler: () => link.Submit(new WorldMutation.RemoveKit(Principal.Console, oversized), new WorldDeferredVerbEchoes(), "edit"));
        var answer = Assert.Single(collection: answers);

        Assert.True(condition: answer.IsError);
        Assert.Contains(actualString: answer.Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "world.transport.codec_refused the submission could not be encoded or decoded: PayloadTooLarge");
    }

    // Submits one Simulation-routed line the way the stdin driver does and applies its tick, returning every result
    // an output observer printed and the registry that counted them.
    private static (List<Puck.Commands.CommandResult> Answers, Puck.Commands.CommandRegistry Registry) DriveOneLine(Func<Puck.Commands.CommandResult> handler) {
        var answers = new List<Puck.Commands.CommandResult>();
        var registry = new Puck.Commands.CommandRegistry(
            modules: [new LineModule(handler: handler)],
            observers: [new AnswerObserver(answers: answers)]
        );
        var router = new Puck.Commands.InputRouter(
            bindings: new NoBindings(),
            principalResolver: new ConsolePrincipal(),
            registry: registry
        );
        var source = new Puck.Commands.TextCommandSource(registry: registry);

        using (var session = source.CreateSession(
            principal: Principal.Console,
            simulationSink: router.ConsoleTextSink
        )) {
            session.Enqueue(line: "line");
            source.Collect();

            var snapshot = router.SnapshotForTick(
                tick: 1UL,
                windowEndTick: ulong.MaxValue
            );

            registry.ApplySnapshot(snapshot: in snapshot);
        }

        router.Dispose();

        return (answers, registry);
    }

    private sealed class LineModule(Func<Puck.Commands.CommandResult> handler) : Puck.Commands.ICommandModule {
        public IEnumerable<Puck.Commands.CommandDefinition> GetCommands() {
            yield return Puck.Commands.CommandDefinition.WithWireArgs(
                bindability: Puck.Commands.CommandBindability.Unbindable,
                description: "Submits the edit under test.",
                handler: (_, _) => handler(),
                name: "line",
                routing: Puck.Commands.CommandRouting.Simulation
            );
        }
    }
    private sealed class AnswerObserver(List<Puck.Commands.CommandResult> answers) : Puck.Commands.ICommandObserver {
        public void OnCommand(in Puck.Commands.CommandActivation activation) {
            if (
                (activation.Text is not null) &&
                !string.IsNullOrEmpty(value: activation.Result.Output)
            ) {
                answers.Add(item: activation.Result);
            }
        }
    }
    private sealed class NoBindings : Puck.Commands.IInputBindings {
        public IReadOnlyList<Puck.Commands.CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : Puck.Commands.IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }
    // An authority that answers through the console's table as a row's echo tap does: a grant inside its submit, any
    // other submission when the law echoes it.
    private sealed class EchoingHost(WorldDeferredVerbEchoes echoes, bool rejectGrants) : IWorldServerHost {
        private long m_last;

        public IDisposable AttachSink(IClientSink sink) => throw new NotSupportedException();
        public void EchoLast(bool grantTable, bool rejected) => Echo(
            echoes: echoes,
            grantTable: grantTable,
            id: m_last,
            rejected: rejected
        );
        public void EnqueueIntent(in IntentSubmission submission) => throw new NotSupportedException();
        public void Submit(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion = null) {
            m_last = envelope.CorrelationId;

            if (envelope.Payload is WorldSubmissionPayload.Grant) {
                EchoLast(
                    grantTable: true,
                    rejected: rejectGrants
                );
            }
        }
    }
    private sealed class CompletionHost : IWorldServerHost {
        public Action<WorldSubmissionResult>? Completion { get; private set; }
        public SubmissionEnvelope Envelope { get; private set; }
        public bool RefuseInline { get; init; }

        public IDisposable AttachSink(IClientSink sink) => throw new NotSupportedException();
        public void EnqueueIntent(in IntentSubmission submission) => throw new NotSupportedException();
        public void Submit(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion = null) {
            Envelope = envelope;
            Completion = completion;
            if (RefuseInline) {
                completion?.Invoke(new WorldSubmissionResult.Refusal(Code: "world.retiring", Detail: "retiring"));
            }
        }
    }

    // A settling session is what observes a settlement; this stands in for one.
    private static void Settled(Puck.Commands.CommandResult result, Action<Puck.Commands.CommandResult> observe) {
        var source = new Puck.Commands.TextCommandSource(registry: new Puck.Commands.CommandRegistry(modules: [new SettlingModule(result: result)]));
        using var session = source.CreateSession(
            onSettled: (_, verdict) => observe(obj: verdict),
            principal: Puck.Commands.Principal.Console
        );

        session.Enqueue(line: "settle");
        source.Collect();
    }

    private sealed class SettlingModule(Puck.Commands.CommandResult result) : Puck.Commands.ICommandModule {
        public IEnumerable<Puck.Commands.CommandDefinition> GetCommands() {
            yield return Puck.Commands.CommandDefinition.WithWireArgs(
                description: "Returns the result under test.",
                bindability: Puck.Commands.CommandBindability.Unbindable,
                handler: (_, _) => result,
                name: "settle"
            );
        }
    }
}
