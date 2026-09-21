using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>
/// Laws for <see cref="WorldDeferredVerbEchoes"/>, the pending-verb table a buffered-mutation verb registers its
/// minted correlation id into so the <c>WorldServer.EchoTap</c> subscriber can print a per-verb refusal line: an
/// entry is taken exactly once, correlation 0 never registers, and the table stays bounded under entries whose
/// verdict never fires. A registration's result settles with the verdict, and on every path where no verdict will
/// arrive, so a session that settles results is never left holding.
/// </summary>
public sealed class DeferredVerbEchoLawTests {
    [Fact]
    public void PendingEntries_EvictOldestPastCapacity() {
        var echoes = new WorldDeferredVerbEchoes();

        for (var id = 1L; (id <= (WorldDeferredVerbEchoes.Capacity + 1)); id++) {
            _ = echoes.Register(
                correlationId: id,
                verb: "world.row.set"
            );
        }

        // The oldest entry fell off the bound; the newest survives.
        Assert.False(condition: echoes.TryTake(
            correlationId: 1,
            settlement: out _,
            verb: out _
        ));
        Assert.True(condition: echoes.TryTake(
            correlationId: (WorldDeferredVerbEchoes.Capacity + 1),
            settlement: out _,
            verb: out _
        ));
    }
    [Fact]
    public void RegisteredEntry_IsTakenExactlyOnce() {
        var echoes = new WorldDeferredVerbEchoes();

        _ = echoes.Register(
            correlationId: 7,
            verb: "world.row.set"
        );

        Assert.True(condition: echoes.TryTake(
            correlationId: 7,
            settlement: out _,
            verb: out var verb
        ));
        Assert.Equal(
            actual: verb,
            expected: "world.row.set"
        );
        Assert.False(condition: echoes.TryTake(
            correlationId: 7,
            settlement: out _,
            verb: out _
        ));
    }
    [Fact]
    public void TakenEntries_DoNotConsumeTheBound() {
        var echoes = new WorldDeferredVerbEchoes();

        // Register-and-take far past the bound, then prove a fresh entry still registers: the evicted-id queue's
        // stale rows never crowd out live ones.
        for (var id = 1L; (id <= (WorldDeferredVerbEchoes.Capacity * 2)); id++) {
            _ = echoes.Register(
                correlationId: id,
                verb: "world.row.set"
            );
            Assert.True(condition: echoes.TryTake(
                correlationId: id,
                settlement: out _,
                verb: out _
            ));
        }

        _ = echoes.Register(
            correlationId: 100_000,
            verb: "world.row.step"
        );

        Assert.True(condition: echoes.TryTake(
            correlationId: 100_000,
            settlement: out _,
            verb: out var verb
        ));
        Assert.Equal(
            actual: verb,
            expected: "world.row.step"
        );
    }
    [Fact]
    public void UnknownCorrelation_TakesNothing() {
        var echoes = new WorldDeferredVerbEchoes();

        Assert.False(condition: echoes.TryTake(
            correlationId: 42,
            settlement: out _,
            verb: out _
        ));
    }
    [Fact]
    public void ZeroCorrelation_NeverRegisters() {
        var echoes = new WorldDeferredVerbEchoes();

        _ = echoes.Register(
            correlationId: 0,
            verb: "world.row.set"
        );

        Assert.False(condition: echoes.TryTake(
            correlationId: 0,
            settlement: out _,
            verb: out _
        ));
    }
    [Fact]
    public void ARegistrationSettlesWithTheVerdictItsTakerGives() {
        var echoes = new WorldDeferredVerbEchoes();
        var result = echoes.Register(
            correlationId: 9,
            verb: "world.row.set"
        );
        Puck.Commands.CommandResult? settled = null;

        Settled(result: result, observe: verdict => settled = verdict);
        Assert.Null(@object: settled);
        Assert.True(condition: echoes.TryTake(
            correlationId: 9,
            settlement: out var settlement,
            verb: out _
        ));
        settlement!.Settle(result: Puck.Commands.CommandResult.Error(output: "[world.row.set: refused]"));

        Assert.True(condition: settled!.Value.IsError);
        Assert.Equal(
            actual: settled.Value.Output,
            expected: "[world.row.set: refused]"
        );
    }
    [Fact]
    public void ARegistrationNoVerdictWillNameSettlesAtOnce() {
        var echoes = new WorldDeferredVerbEchoes();
        Puck.Commands.CommandResult? settled = null;

        Settled(
            observe: verdict => settled = verdict,
            result: echoes.Register(
                correlationId: 0,
                verb: "world.row.set"
            )
        );

        Assert.True(condition: settled!.Value.IsError);
        Assert.Contains(expectedSubstring: "no local verdict", actualString: settled.Value.Output, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void AnEvictedRegistrationSettlesAsAnUnknownOutcome() {
        var echoes = new WorldDeferredVerbEchoes();
        Puck.Commands.CommandResult? settled = null;

        Settled(
            observe: verdict => settled = verdict,
            result: echoes.Register(
                correlationId: 1,
                verb: "world.row.set"
            )
        );
        for (var id = 2L; (id <= (WorldDeferredVerbEchoes.Capacity + 1)); id++) {
            _ = echoes.Register(
                correlationId: id,
                verb: "world.row.set"
            );
        }

        Assert.True(condition: settled!.Value.IsError);
        Assert.Contains(
            actualString: settled.Value.Output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "no verdict arrived"
        );
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

        Settled(first.Submit(new WorldMutation.RemoveKit(WorldPrincipal.Console, "one"), echoes, "first"), result => firstResult = result);
        Settled(second.Submit(new WorldMutation.RemoveKit(WorldPrincipal.Console, "two"), echoes, "second"), result => secondResult = result);
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

        Settled(link.Submit(new WorldMutation.RemoveKit(WorldPrincipal.Console, "one"), new WorldDeferredVerbEchoes(), "edit"), verdict => result = verdict);
        Assert.True(condition: result!.Value.IsError);
        Assert.Contains("retiring", result.Value.Output, StringComparison.Ordinal);
    }
    [Fact]
    public void CodecRefusalSettlesWithoutEverReachingTheAuthority() {
        var host = new CompletionHost();
        var link = new LoopbackTransport(server: host);
        Puck.Commands.CommandResult? result = null;
        var oversized = new string(c: 'x', count: (WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Mutation) + 1));

        Settled(link.Submit(new WorldMutation.RemoveKit(WorldPrincipal.Console, oversized), new WorldDeferredVerbEchoes(), "edit"), verdict => result = verdict);
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
            ? link.SubmitUndo(1, WorldPrincipal.Console, echoes, "world.undo")
            : link.SubmitRebuild(new WorldRebuildRequest(WorldRebuildKind.Reset, null, null, false), WorldPrincipal.Console, echoes, "world.reset"));

        Settled(pending, verdict => result = verdict);
        Assert.True(condition: result!.Value.IsError);
        Assert.Contains("retiring", result.Value.Output, StringComparison.Ordinal);
        Assert.False(condition: echoes.TryTake(host.Envelope.CorrelationId, out _, out _));
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
            principal: Puck.Commands.CommandPrincipal.Console
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
