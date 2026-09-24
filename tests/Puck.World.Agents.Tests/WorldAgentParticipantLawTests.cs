using Puck.Commands;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Puck.Abstractions;
using Puck.World.Agents.Harness;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Agents.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the <c>agent.harness</c> participant type. A host-approved configuration selects it by type and
/// a chat client provider by name; its loop observes and acts on its body only through <see cref="WorldAgentBridge"/>,
/// only while the host pumps it, offers the model action tools only under an allowing policy, stops when the host
/// disposes it, and every bad configuration is refused at composition by name.
/// </summary>
public sealed class WorldAgentParticipantLawTests {
    private static readonly string[] ActionTools = ["puck_move", "puck_press_channel", "puck_stop"];
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(seconds: 30);

    private static WorldChannelTable Channels() => WorldChannelTable.Compile(channels: [
        new WorldChannel(
            Name: "advance",
            Role: ChannelRole.MoveAdvance,
            Shape: ChannelShape.Bipolar
        ),
    ]);
    private static PuckExtensionSet Compose(params string[] providers) => PuckExtensionSet.Compose(extensions: [
        new WorldAgentHarnessExtension(),
        .. providers.Select(selector: static name => ((IPuckExtension)new ProviderExtension(name: name))),
    ]);
    private static WorldParticipantContext Context(PuckExtensionSet extensions, RecordingLink link, Principal? principal = null) => new() {
        BodyIndex = 3,
        Channels = Channels,
        Clock = TimeProvider.System,
        Extensions = extensions,
        Link = link,
        Name = "guide",
        Principal = (principal ?? Principal.Addon(name: "guide")),
    };
    private static IWorldParticipant Create(PuckExtensionSet extensions, RecordingLink link, string settings, Principal? principal = null) =>
        extensions.Select<WorldParticipantType>(
            key: WorldAgentParticipant.Type,
            purpose: "Participant type"
        ).Create(
            arg1: Context(
                extensions: extensions,
                link: link,
                principal: principal
            ),
            arg2: JsonDocument.Parse(json: settings).RootElement.Clone()
        );
    private static string Refusal(PuckExtensionSet extensions, string settings, Principal? principal = null) =>
        Assert.ThrowsAny<ArgumentException>(testCode: () => Create(
            extensions: extensions,
            link: new RecordingLink(),
            principal: principal,
            settings: settings
        )).Message;
    // Pumps the participant the way a host does — at closed boundaries, from one thread — until its worker finishes.
    private static async Task PumpUntilCompleteAsync(WorldAgentParticipant participant) {
        using var deadline = new CancellationTokenSource(delay: Deadline);
        var tick = 0UL;

        while (!participant.Completion.IsCompleted) {
            participant.Pump(completedTick: ++tick);
            await Task.Delay(
                cancellationToken: deadline.Token,
                millisecondsDelay: 1
            );
        }
        await participant.Completion;
    }
    private static async Task<(WorldAgentParticipant Participant, RecordingLink Link, ScriptedChatClient Model)> RunOneTurnAsync(string approval) {
        var extensions = Compose("scripted");
        var link = new RecordingLink();
        var participant = Assert.IsType<WorldAgentParticipant>(@object: Create(
            extensions: extensions,
            link: link,
            settings: $$"""{ "objective": "Walk to the gate.", "approval": "{{approval}}", "maximumTurns": 1, "planning": false, "telemetry": false }"""
        ));

        participant.Start();
        await PumpUntilCompleteAsync(participant: participant);
        return (participant, link, ProviderExtension.Clients["scripted"]);
    }

    [Fact]
    public async Task TheLoopObservesItsBodyAsItsPrincipalOnlyWhenPumped() {
        var extensions = Compose("scripted");
        var link = new RecordingLink();
        await using var participant = Assert.IsType<WorldAgentParticipant>(@object: Create(
            extensions: extensions,
            link: link,
            settings: """{ "objective": "Walk to the gate.", "maximumTurns": 1, "planning": false, "telemetry": false }"""
        ));

        participant.Start();
        // The model's first call observes; without a pump the observation stays queued and the link is untouched.
        await ProviderExtension.Clients["scripted"].ObservationRequested.Task.WaitAsync(timeout: Deadline, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(@object: link.LastQuery);

        await PumpUntilCompleteAsync(participant: participant);

        Assert.Equal(
            expected: new WorldQuery.PlayerWhere(Index: 3),
            actual: link.LastQuery
        );
        Assert.All(
            action: static principal => Assert.Equal(
                expected: Principal.Addon(name: "guide"),
                actual: principal
            ),
            collection: link.QueryPrincipals
        );
        Assert.Equal(
            expected: 1L,
            actual: participant.Turns
        );
    }
    [Fact]
    public async Task AnAllowingPolicyOffersTheActionsWithoutApprovalAndTheBridgeSubmitsThem() {
        var (participant, link, model) = await RunOneTurnAsync(approval: "allow");

        await using (participant) {
            var command = Assert.IsType<WorldCommand.EnqueueSegment>(@object: Assert.IsType<WorldSubmissionPayload.Command>(@object: link.LastPayload).Value);

            Assert.Equal(
                expected: 3,
                actual: command.EntityIndex
            );
            Assert.Equal(
                expected: Principal.Addon(name: "guide"),
                actual: link.LastPrincipal
            );
            foreach (var name in ActionTools) {
                var tool = Assert.Single(
                    collection: model.OfferedTools,
                    predicate: tool => (tool.Name == name)
                );

                Assert.IsNotType<ApprovalRequiredAIFunction>(@object: tool);
            }
        }
    }
    [Fact]
    public async Task ARefusingPolicyOffersNoActionToolsSoNothingReachesTheWorld() {
        var (participant, link, model) = await RunOneTurnAsync(approval: "refuse");

        await using (participant) {
            Assert.Null(@object: link.LastPayload);
            Assert.Contains(
                collection: model.OfferedTools,
                filter: static tool => (tool.Name == "puck_observe_body")
            );
            Assert.Contains(
                collection: model.OfferedTools,
                filter: static tool => (tool.Name == "puck_get_affordances")
            );
            Assert.DoesNotContain(
                collection: model.OfferedTools,
                filter: static tool => ActionTools.Contains(value: tool.Name)
            );
        }
    }
    [Fact]
    public async Task DisposingTheParticipantStopsItsLoopBetweenTurns() {
        var extensions = Compose("scripted");
        var link = new RecordingLink();
        var participant = Assert.IsType<WorldAgentParticipant>(@object: Create(
            extensions: extensions,
            link: link,
            settings: """{ "objective": "Walk to the gate.", "turnSeconds": 3600, "planning": false, "telemetry": false }"""
        ));

        participant.Start();
        using (var deadline = new CancellationTokenSource(delay: Deadline)) {
            var tick = 0UL;

            while (participant.Turns == 0) {
                participant.Pump(completedTick: ++tick);
                await Task.Delay(
                    cancellationToken: deadline.Token,
                    millisecondsDelay: 1
                );
            }
        }
        Assert.False(condition: participant.Completion.IsCompleted);
        Assert.Contains(
            actualString: participant.Describe(),
            expectedSubstring: "state=running"
        );

        await participant.DisposeAsync().AsTask().WaitAsync(timeout: Deadline, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: participant.Completion.IsCompletedSuccessfully);
        Assert.Contains(
            actualString: participant.Describe(),
            expectedSubstring: "state=stopped turns=1"
        );
        Assert.True(condition: ProviderExtension.Clients["scripted"].Disposed);
    }
    [InlineData(
        """{ "objective": "x", "provider": "missing" }""",
        "Agent chat client provider 'missing' is not installed; name one of: alpha, beta."
    )]
    [InlineData(
        """{ "objective": "x" }""",
        "Agent chat client provider: more than one installed extension provides a ChatClientProvider ('alpha' from provider.alpha, 'beta' from provider.beta); name one."
    )]
    [InlineData(
        """{ "objective": "x", "provider": "alpha", "apiKey": "secret" }""",
        "'apiKey'"
    )]
    [InlineData(
        """{ "objective": " " }""",
        "needs a non-blank objective."
    )]
    [InlineData(
        """{ "objective": "x", "provider": "alpha", "approval": "sometimes" }""",
        "approval 'sometimes' is not 'refuse' or 'allow'."
    )]
    [InlineData(
        """{ "objective": "x", "provider": "alpha", "turnSeconds": 0 }""",
        "turnSeconds must be a positive finite number of seconds."
    )]
    [InlineData(
        """{ "objective": "x", "provider": "alpha", "providerSettings": { "reject": true } }""",
        "provider 'alpha' settings are invalid: the scripted provider refuses these settings."
    )]
    [Theory]
    public void BadConfigurationIsRefusedByName(string settings, string expected) {
        var message = Refusal(
            extensions: Compose("alpha", "beta"),
            settings: settings
        );

        Assert.StartsWith(
            actualString: message,
            expectedStartString: "Participant 'guide' (agent.harness) "
        );
        Assert.Contains(
            actualString: message,
            expectedSubstring: expected
        );
    }
    [Fact]
    public void NoInstalledProviderAndTheConsolePrincipalAreRefusedByName() {
        Assert.Contains(
            actualString: Refusal(
                extensions: Compose(),
                settings: """{ "objective": "x" }"""
            ),
            expectedSubstring: "Agent chat client provider: no installed extension provides a ChatClientProvider."
        );
        Assert.Contains(
            actualString: Refusal(
                extensions: Compose("alpha"),
                principal: Principal.Console,
                settings: """{ "objective": "x" }"""
            ),
            expectedSubstring: "principal 'console' must be a seat, addon, or peer"
        );
    }

    private sealed class ProviderExtension(string name) : IPuckExtension {
        public static Dictionary<string, ScriptedChatClient> Clients { get; } = new(comparer: StringComparer.Ordinal);
        public string Name => $"provider.{name}";

        public void Register(IPuckExtensionRegistry registry) => registry.AddChatClient(
            name: name,
            provider: new ChatClientProvider(Create: settings => {
                if (settings.TryGetProperty(
                    propertyName: "reject",
                    value: out _
                )) {
                    throw new ArgumentException(message: "the scripted provider refuses these settings.");
                }
                var client = new ScriptedChatClient();

                lock (Clients) { Clients[name] = client; }
                return client;
            })
        );
    }
    /// <summary>A model that observes, then moves, then finishes: the smallest turn that exercises a read, an action
    /// the policy either offers or withholds, and the loop's end.</summary>
    private sealed class ScriptedChatClient : IChatClient {
        private int m_calls;

        public bool Disposed { get; private set; }

        public TaskCompletionSource ObservationRequested { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AITool> OfferedTools { get; private set; } = [];

        public void Dispose() => Disposed = true;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) {
            if (options?.Tools is { } tools) { OfferedTools = [.. tools]; }
            var call = Interlocked.Increment(location: ref m_calls);
            AIContent content = call switch {
                1 => new FunctionCallContent(
                    arguments: new Dictionary<string, object?> { ["aspect"] = "pose" },
                    callId: "observe",
                    name: "puck_observe_body"
                ),
                2 => new FunctionCallContent(
                    arguments: new Dictionary<string, object?> {
                        ["forward"] = 1d,
                        ["strafe"] = 0d,
                        ["up"] = 0d,
                        ["yaw"] = 0d,
                        ["pitch"] = 0d,
                        ["roll"] = 0d,
                        ["seconds"] = 1d,
                    },
                    callId: "move",
                    name: "puck_move"
                ),
                _ => new TextContent(text: "done"),
            };

            if (call == 1) { ObservationRequested.TrySetResult(); }
            return Task.FromResult(result: new ChatResponse(message: new ChatMessage(
                contents: [content],
                role: ChatRole.Assistant
            )));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => (serviceType.IsInstanceOfType(o: this)
            ? this
            : null
        );
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
    private sealed class RecordingLink : IPrincipalServerLink {
        public WorldSubmissionPayload? LastPayload { get; private set; }
        public Principal LastPrincipal { get; private set; }
        public WorldQuery? LastQuery { get; private set; }
        public List<Principal> QueryPrincipals { get; } = [];

        public void Query(WorldQuery query, Action<QueryAnswer> completion) => throw new NotSupportedException();
        public void Query(WorldQuery query, Principal principal, Action<QueryAnswer> completion) {
            LastQuery = query;
            QueryPrincipals.Add(item: principal);
            completion(new QueryAnswer(Text: "at the gate"));
        }
        public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) {
            LastPayload = payload;
            LastPrincipal = principal;
            return 1L;
        }
        public void SubmitIntent(in IntentSubmission submission) => throw new NotSupportedException();
        public void SubmitSession(SessionRequest request, Action<SessionReply> completion) => throw new NotSupportedException();
    }
}
