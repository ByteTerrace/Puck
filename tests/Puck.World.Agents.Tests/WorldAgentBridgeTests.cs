using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Agents.Harness;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Agents.Tests;

public sealed class WorldAgentBridgeTests {
    private static WorldAgentBridge Bridge(RecordingLink link, WorldPrincipal principal, int bodyIndex) => new(
        bodyIndex: bodyIndex,
        channels: Channels,
        dispatcher: InlineDispatcher.Instance,
        link: link,
        principal: principal
    );
    private static WorldChannelTable Channels() => WorldChannelTable.Compile(channels: [
        new WorldChannel(
            Name: "advance",
            Role: ChannelRole.MoveAdvance,
            Shape: ChannelShape.Bipolar
        ),
        new WorldChannel(
            Name: "turn",
            Role: ChannelRole.Turn,
            Shape: ChannelShape.Bipolar
        ),
        new WorldChannel(
            Composition: true,
            Name: "jump",
            Shape: ChannelShape.Binary
        ),
    ]);
    private static WorldChannelTable Channels(string name) => WorldChannelTable.Compile(channels: [
        new WorldChannel(
            Name: name,
            Role: ChannelRole.MoveAdvance,
            Shape: ChannelShape.Bipolar
        ),
    ]);

    [Fact]
    public async Task ActionsRejectDurationsThatCannotCrossTheProtocol() {
        var bridge = Bridge(
            bodyIndex: 4,
            link: new RecordingLink(),
            principal: WorldPrincipal.Peer(
                generation: 1,
                index: 4
            )
        );

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(testCode: async () =>
            await bridge.MoveAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            forward: 1d,
            pitch: 0d,
            roll: 0d,
            seconds: double.MaxValue,
            strafe: 0d,
            up: 0d,
            yaw: 0d
        ));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(testCode: async () =>
            await bridge.PressAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            channel: "jump",
            holdSeconds: 0d
        ));
    }
    [Fact]
    public async Task AffordancesUseAuthorityVerdictsAndRefreshChannels() {
        var link = new RecordingLink();
        var current = Channels(name: "advance");
        var bridge = new WorldAgentBridge(
            bodyIndex: 4,
            channels: () => current,
            dispatcher: InlineDispatcher.Instance,
            link: link,
            principal: WorldPrincipal.Peer(
                generation: 1,
                index: 4
            )
        );

        var first = await bridge.GetAffordancesAsync(cancellationToken: TestContext.Current.CancellationToken);

        current = Channels(name: "thrust");
        var second = await bridge.GetAffordancesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: first.CanObserve);
        Assert.True(condition: first.CanDrive);
        Assert.Equal(
            expected: "advance",
            actual: Assert.Single(collection: first.Channels).Name
        );
        Assert.Equal(
            expected: "thrust",
            actual: Assert.Single(collection: second.Channels).Name
        );
        Assert.All(
            collection: link.QueryPrincipals,
            action: value => Assert.Equal(
                expected: bridge.Principal,
                actual: value
            )
        );
    }
    [Fact]
    public void AgentHostCompositionIsAnExplicitExtension() {
        var services = new ServiceCollection();

        services.AddPuckWorldAgentBridge(
            mailboxCapacity: 4,
            maximumOperationsPerFrame: 2
        );

        Assert.Single(
            collection: services,
            predicate: static descriptor => (descriptor.ServiceType == typeof(IPrincipalServerLink))
        );
        Assert.Single(
            collection: services,
            predicate: static descriptor => (descriptor.ServiceType == typeof(WorldAgentMailbox))
        );
        Assert.Single(
            collection: services,
            predicate: static descriptor => (descriptor.ServiceType == typeof(IWorldAgentDispatcher))
        );
        Assert.Single(
            collection: services,
            predicate: static descriptor => (descriptor.ServiceType == typeof(ISnapshotInputCapture))
        );
    }
    [Fact]
    public async Task BridgeDoesNotTouchLinkBeforeHostMailboxCapture() {
        var link = new RecordingLink();
        using var mailbox = new WorldAgentMailbox(
            capacity: 4,
            maximumOperationsPerFrame: 2
        );
        var bridge = new WorldAgentBridge(
            bodyIndex: 7,
            channels: Channels,
            dispatcher: mailbox,
            link: link,
            principal: WorldPrincipal.Peer(
                generation: 3,
                index: 7
            )
        );

        var pending = bridge.ObserveAsync(
            kind: WorldAgentObservationKind.Pose,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(condition: pending.IsCompleted);
        Assert.Null(@object: link.LastQuery);

        mailbox.CaptureFrame(frameKey: 1);

        Assert.Equal(
            expected: WorldAgentObservationKind.Pose,
            actual: (await pending).Kind
        );
        Assert.IsType<WorldQuery.PlayerWhere>(@object: link.LastQuery);
    }
    [Fact]
    public void ConstructorRefusesNonActingPrincipal() {
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new WorldAgentBridge(
            bodyIndex: 0,
            channels: static () => WorldChannelTable.Empty,
            dispatcher: InlineDispatcher.Instance,
            link: new RecordingLink(),
            principal: WorldPrincipal.World
        ));
    }
    [Fact]
    public async Task HarnessLoadsSkillsOnlyFromAnExplicitSource() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-agent-skills-");

        try {
            var skillDirectory = Directory.CreateDirectory(path: Path.Combine(
                path1: root.FullName,
                path2: "engine-expert"
            ));

            await File.WriteAllTextAsync(
                path: Path.Combine(
                    path1: skillDirectory.FullName,
                    path2: "SKILL.md"
                ),
                contents: """
                    ---
                    name: engine-expert
                    description: Explains the engine's public simulation concepts.
                    ---
                    Treat Puck's protocol as authoritative.
                    """,
                cancellationToken: TestContext.Current.CancellationToken
            );
            using var skills = new AgentFileSkillsSource(skillPath: root.FullName);
            var chatClient = new RecordingChatClient();
            var agent = WorldAgentHarness.Create(
                bridge: Bridge(
                    link: new RecordingLink(),
                    principal: WorldPrincipal.Peer(
                        generation: 1,
                        index: 4
                    ),
                    bodyIndex: 4
                ),
                chatClient: chatClient,
                options: new WorldAgentHarnessOptions {
                    EnableOpenTelemetry = false,
                    EnablePlanning = false,
                    SkillsSource = skills,
                }
            );
            var session = await agent.CreateSessionAsync(cancellationToken: TestContext.Current.CancellationToken);

            _ = await agent.RunAsync(
                message: "Which skills are available?",
                session: session,
                cancellationToken: TestContext.Current.CancellationToken
            );

            var tools = Assert.IsAssignableFrom<IList<AITool>>(@object: chatClient.LastOptions?.Tools);

            Assert.Contains(
                collection: tools,
                filter: static tool => (tool.Name == AgentSkillsProvider.LoadSkillToolName)
            );
        } finally {
            root.Delete(recursive: true);
        }
    }
    [Fact]
    public async Task HarnessPublishesConstrainedToolsAndApprovalWrapsActions() {
        var chatClient = new RecordingChatClient();
        var bridge = Bridge(
            link: new RecordingLink(),
            principal: WorldPrincipal.Peer(
                generation: 1,
                index: 4
            ),
            bodyIndex: 4
        );
        var agent = WorldAgentHarness.Create(
            bridge: bridge,
            chatClient: chatClient,
            options: new WorldAgentHarnessOptions {
                EnableOpenTelemetry = false,
                EnablePlanning = false,
            }
        );
        var session = await agent.CreateSessionAsync(cancellationToken: TestContext.Current.CancellationToken);

        _ = await agent.RunAsync(
            message: "Inspect the world.",
            session: session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var tools = Assert.IsAssignableFrom<IList<AITool>>(@object: chatClient.LastOptions?.Tools);

        Assert.IsAssignableFrom<AIFunction>(@object: Assert.Single(
            collection: tools,
            predicate: tool => (tool.Name == "puck_observe_body")
        ));
        Assert.IsAssignableFrom<AIFunction>(@object: Assert.Single(
            collection: tools,
            predicate: tool => (tool.Name == "puck_get_affordances")
        ));
        Assert.IsType<ApprovalRequiredAIFunction>(@object: Assert.Single(
            collection: tools,
            predicate: tool => (tool.Name == "puck_move")
        ));
        Assert.IsType<ApprovalRequiredAIFunction>(@object: Assert.Single(
            collection: tools,
            predicate: tool => (tool.Name == "puck_press_channel")
        ));
        Assert.IsType<ApprovalRequiredAIFunction>(@object: Assert.Single(
            collection: tools,
            predicate: tool => (tool.Name == "puck_stop")
        ));
        Assert.DoesNotContain(
            collection: tools,
            filter: static tool => (tool is HostedWebSearchTool)
        );
    }
    [Fact]
    public void LoopbackPrincipalQueryStampsExplicitIdentity() {
        var host = new RecordingHost();
        var link = new LoopbackTransport(server: host);
        var principal = WorldPrincipal.Peer(
            generation: 4,
            index: 9
        );
        QueryAnswer answer = default;

        link.Query(
            completion: value => answer = value,
            principal: principal,
            query: new WorldQuery.PlayerWhere(Index: 9)
        );

        Assert.Equal(
            expected: principal,
            actual: host.LastEnvelope?.Principal
        );
        Assert.Equal(
            expected: "host answer",
            actual: answer.Text
        );
    }
    [Fact]
    public async Task MailboxCancellationPreventsQueuedWorldOperation() {
        using var mailbox = new WorldAgentMailbox(
            capacity: 1,
            maximumOperationsPerFrame: 1
        );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);
        var invoked = false;
        var pending = mailbox.InvokeAsync(
            operation: () => {
                invoked = true;
                return 42;
            },
            cancellationToken: cancellation.Token
        );

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: async () => await pending);
        mailbox.CaptureFrame(frameKey: 1);
        Assert.False(condition: invoked);
    }
    [Fact]
    public async Task MailboxDefersWorkToHostCaptureAndRefusesOverflow() {
        using var mailbox = new WorldAgentMailbox(
            capacity: 1,
            maximumOperationsPerFrame: 1
        );
        var captureThread = Environment.CurrentManagedThreadId;
        var executionThread = -1;
        var pending = mailbox.InvokeAsync(
            operation: () => {
                executionThread = Environment.CurrentManagedThreadId;
                return 42;
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        var overflow = mailbox.InvokeAsync(
            operation: static () => 0,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(condition: pending.IsCompleted);
        Assert.Equal(
            expected: 1,
            actual: mailbox.PendingCount
        );
        _ = await Assert.ThrowsAsync<InvalidOperationException>(testCode: async () => await overflow);

        mailbox.CaptureFrame(frameKey: 7);

        Assert.Equal(
            expected: 42,
            actual: await pending
        );
        Assert.Equal(
            actual: executionThread,
            expected: captureThread
        );
        Assert.Equal(
            expected: 0,
            actual: mailbox.PendingCount
        );
    }
    [Fact]
    public async Task MailboxShutdownRefusesQueuedAndFutureWork() {
        var mailbox = new WorldAgentMailbox(
            capacity: 2,
            maximumOperationsPerFrame: 1
        );
        var pending = mailbox.InvokeAsync(
            operation: static () => 42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        mailbox.Dispose();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(testCode: async () => await pending);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(testCode: async () =>
            await mailbox.InvokeAsync(
            operation: static () => 0,
            cancellationToken: TestContext.Current.CancellationToken
        ));
    }
    [Fact]
    public async Task MoveBuildsTypedIntentAndReturnsSubmissionNotAcceptance() {
        var link = new RecordingLink { CorrelationId = 91 };
        var principal = WorldPrincipal.Peer(
            generation: 2,
            index: 5
        );
        var bridge = Bridge(
            bodyIndex: 5,
            link: link,
            principal: principal
        );

        var receipt = await bridge.MoveAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            forward: 0.75d,
            strafe: 0d,
            up: 0d,
            yaw: -0.25d,
            pitch: 0d,
            roll: 0d,
            seconds: 0.5d
        );

        var payload = Assert.IsType<WorldSubmissionPayload.Command>(@object: link.LastPayload);
        var command = Assert.IsType<WorldCommand.EnqueueSegment>(@object: payload.Value);

        Assert.Equal(
            expected: principal,
            actual: command.Principal
        );
        Assert.Equal(
            expected: 5,
            actual: command.EntityIndex
        );
        Assert.Equal(
            expected: FixedQ4816.FromDouble(value: 0.75d),
            actual: command.Intent[0]
        );
        Assert.Equal(
            expected: FixedQ4816.FromDouble(value: -0.25d),
            actual: command.Intent[1]
        );
        Assert.True(condition: receipt.Correlated);
        Assert.Equal(
            expected: 91,
            actual: receipt.CorrelationId
        );
        Assert.Contains(
            expectedSubstring: "does not claim",
            actualString: receipt.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public async Task ObservationCarriesScopedPrincipalAndBodyCoordinate() {
        var link = new RecordingLink();
        var principal = WorldPrincipal.Peer(
            generation: 3,
            index: 7
        );
        var bridge = Bridge(
            bodyIndex: 7,
            link: link,
            principal: principal
        );

        var observation = await bridge.ObserveAsync(
            kind: WorldAgentObservationKind.Contacts,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(
            expected: principal,
            actual: link.LastQueryPrincipal
        );
        var contacts = Assert.IsType<WorldQuery.Contacts>(@object: link.LastQuery);

        Assert.Equal(
            expected: 8,
            actual: contacts.Index
        );
        Assert.Equal(
            expected: 7,
            actual: observation.BodyIndex
        );
        Assert.Equal(
            expected: "authoritative answer",
            actual: observation.Text
        );
    }
    [Fact]
    public async Task ObservationHonorsCallerCancellation() {
        var link = new RecordingLink { CompleteQueries = false };
        var bridge = Bridge(
            bodyIndex: 4,
            link: link,
            principal: WorldPrincipal.Peer(
                generation: 1,
                index: 4
            )
        );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: async () =>
            await bridge.ObserveAsync(
            kind: WorldAgentObservationKind.Pose,
            cancellationToken: cancellation.Token
        ));
    }
    [Fact]
    public async Task PressResolvesLiveNameAndRejectsUnknownChannel() {
        var link = new RecordingLink();
        var bridge = Bridge(
            link: link,
            principal: WorldPrincipal.Addon(name: "guide"),
            bodyIndex: 1
        );

        _ = await bridge.PressAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            channel: "jump",
            holdSeconds: null,
            value: 1d
        );

        var payload = Assert.IsType<WorldSubmissionPayload.Command>(@object: link.LastPayload);
        var command = Assert.IsType<WorldCommand.PressChannel>(@object: payload.Value);

        Assert.Equal(
            expected: 2,
            actual: command.ChannelOrdinal
        );
        _ = await Assert.ThrowsAsync<ArgumentException>(testCode: async () =>
            await bridge.PressAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            channel: "invented"
        ));
    }

    private sealed class RecordingLink : IPrincipalServerLink {
        public bool CompleteQueries { get; init; } = true;
        public long CorrelationId { get; init; } = 1;
        public WorldSubmissionPayload? LastPayload { get; private set; }
        public WorldQuery? LastQuery { get; private set; }
        public WorldPrincipal LastQueryPrincipal { get; private set; }
        public List<WorldPrincipal> QueryPrincipals { get; } = [];

        public void Query(WorldQuery query, Action<QueryAnswer> completion) => Query(
            completion: completion,
            principal: WorldPrincipal.Console,
            query: query
        );
        public void Query(WorldQuery query, WorldPrincipal principal, Action<QueryAnswer> completion) {
            LastQuery = query;
            LastQueryPrincipal = principal;
            QueryPrincipals.Add(item: principal);
            if (!CompleteQueries) {
                return;
            }

            object? payload = ((query is WorldQuery.GrantAllows)
                ? new GrantVerdict(Rule: GrantRule.ConcreteHold)
                : null
            );

            completion(new QueryAnswer(
                Payload: payload,
                Text: "authoritative answer"
            ));
        }
        public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal) {
            LastPayload = payload;
            return CorrelationId;
        }
        public void SubmitIntent(in IntentSubmission submission) => throw new NotSupportedException();
        public void SubmitSession(SessionRequest request, Action<SessionReply> completion) => throw new NotSupportedException();
    }
    private sealed class InlineDispatcher : IWorldAgentDispatcher {
        public static InlineDispatcher Instance { get; } = new();

        public ValueTask<TResult> InvokeAsync<TResult>(
            Func<TResult> operation,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result: operation());
        }
    }
    private sealed class RecordingChatClient : IChatClient {
        public ChatOptions? LastOptions { get; private set; }

        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) {
            LastOptions = options;

            return Task.FromResult(result: new ChatResponse(message: new ChatMessage(
                role: ChatRole.Assistant,
                content: "done"
            )));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => (serviceType.IsInstanceOfType(o: this)
            ? this
            : null
        );
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        ) {
            LastOptions = options;
            await Task.CompletedTask.ConfigureAwait(continueOnCapturedContext: false);
            yield return new ChatResponseUpdate(
                role: ChatRole.Assistant,
                content: "done"
            );
        }
    }
    private sealed class RecordingHost : IWorldServerHost {
        public SubmissionEnvelope? LastEnvelope { get; private set; }

        public IDisposable AttachSink(IClientSink sink) => new NoopDisposable();
        public void EnqueueIntent(in IntentSubmission submission) => throw new NotSupportedException();
        public void Submit(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion = null) {
            LastEnvelope = envelope;
            completion?.Invoke(obj: new WorldSubmissionResult.Query(Answer: new QueryAnswer(Text: "host answer")));
        }

        private sealed class NoopDisposable : IDisposable {
            public void Dispose() { }
        }
    }
}
