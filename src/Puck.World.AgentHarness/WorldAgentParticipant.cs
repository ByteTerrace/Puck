using Puck.Commands;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Puck.World.Protocol;

namespace Puck.World.Agents.Harness;

/// <summary>The <c>agent.harness</c> participant: a Harness agent over a selected <see cref="ChatClientProvider"/> that
/// drives one body through <see cref="WorldAgentBridge"/> in turns until its host stops it.</summary>
/// <remarks>Model inference and Harness orchestration run on the participant's own worker. Each turn sends the
/// objective (then a continuation) and waits <c>turnSeconds</c> on the host clock before the next. The configured
/// <c>approval</c> is the consent: <c>allow</c> offers the action tools without a Harness approval step, and
/// <c>refuse</c> offers none, so the participant only observes. Bridge reads and actions queue on a bounded
/// <see cref="WorldAgentMailbox"/> that only <see cref="Pump"/> drains, so every world operation runs on the simulation
/// thread at a closed boundary. Puck's grants decide every read and action independently of that consent.</remarks>
public sealed class WorldAgentParticipant : IWorldParticipant {
    /// <summary>The participant type key a configuration selects.</summary>
    public const string Type = "agent.harness";

    private const string Continuation = "Host time has passed since your last turn, so the world may have changed. Continue toward your objective.";
    private const int MaximumFailureCharacters = 256;

    private readonly HarnessAgent m_agent;
    private readonly IChatClient m_chatClient;
    private readonly TimeProvider m_clock;
    private readonly WorldAgentMailbox m_mailbox;
    private readonly int? m_maximumTurns;
    private readonly string m_objective;
    private readonly string m_provider;

    private readonly CancellationTokenSource m_stop = new();

    private readonly TimeSpan m_turnInterval;
    private readonly WorldAgentBridge m_bridge;

    private int m_disposed;
    private string? m_lastFailure;

    private Task m_loop = Task.CompletedTask;

    private int m_started;
    private int m_state;
    private long m_turns;

    private WorldAgentParticipant(WorldParticipantContext context, WorldAgentParticipantSettings settings, string provider, IChatClient chatClient) {
        m_mailbox = new WorldAgentMailbox();
        m_bridge = new WorldAgentBridge(
            bodyIndex: context.BodyIndex,
            channels: context.Channels,
            dispatcher: m_mailbox,
            link: context.Link,
            principal: context.Principal
        );
        m_agent = WorldAgentHarness.Create(
            bridge: m_bridge,
            chatClient: chatClient,
            options: new WorldAgentHarnessOptions {
                Actions = ((settings.Approval == "allow")
                    ? WorldAgentActions.Unattended
                    : WorldAgentActions.None),
                EnableOpenTelemetry = settings.Telemetry,
                EnablePlanning = settings.Planning,
                Instructions = settings.Instructions,
                MaximumIterationsPerRequest = settings.MaximumIterationsPerRequest,
                Name = context.Name,
            }
        );
        m_chatClient = chatClient;
        m_clock = context.Clock;
        m_maximumTurns = settings.MaximumTurns;
        m_objective = settings.Objective;
        m_provider = provider;
        m_turnInterval = TimeSpan.FromSeconds(value: settings.TurnSeconds);
    }

    /// <summary>Gets the most recent turn failure, bounded and without provider credentials, or <see langword="null"/>.</summary>
    public string? LastFailure => Volatile.Read(location: ref m_lastFailure);
    /// <summary>Gets the number of completed turns.</summary>
    public long Turns => Interlocked.Read(location: ref m_turns);
    /// <summary>Gets the worker's completion: running until the configured turn count is reached or the host stops the
    /// participant.</summary>
    public Task Completion => m_loop;

    private static ArgumentException Refusal(WorldParticipantContext context, string detail) =>
        new(message: $"Participant '{context.Name}' ({Type}) {detail}");

    /// <summary>Validates settings, selects the chat client provider, and builds the Harness over a bridge to the
    /// participant's body. Makes no service call and starts no work.</summary>
    /// <param name="context">The host-supplied participant context.</param>
    /// <param name="settings">The participant's settings object.</param>
    /// <returns>The participant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A setting is missing, unknown, or out of range; the principal is not a
    /// seat, addon, or peer; or the provider is not installed, or none is named and none or several are installed.
    /// Every refusal names the participant and the offending setting or provider.</exception>
    public static IWorldParticipant Create(WorldParticipantContext context, JsonElement settings) {
        ArgumentNullException.ThrowIfNull(argument: context);
        WorldAgentParticipantSettings parsed;

        try {
            parsed = (settings.Deserialize(jsonTypeInfo: WorldAgentParticipantJson.Default.WorldAgentParticipantSettings)
                ?? throw new JsonException(message: "settings must be an object."));
        } catch (JsonException error) {
            throw Refusal(
                context: context,
                detail: $"settings are invalid: {error.Message}"
            );
        }
        if (string.IsNullOrWhiteSpace(value: parsed.Objective)) {
            throw Refusal(
                context: context,
                detail: "needs a non-blank objective."
            );
        }
        if (parsed.Approval is not ("refuse" or "allow")) {
            throw Refusal(
                context: context,
                detail: $"approval '{parsed.Approval}' is not 'refuse' or 'allow'."
            );
        }
        if (!double.IsFinite(d: parsed.TurnSeconds) || (parsed.TurnSeconds <= 0d) || (parsed.TurnSeconds > TimeSpan.MaxValue.TotalSeconds)) {
            throw Refusal(
                context: context,
                detail: "turnSeconds must be a positive finite number of seconds."
            );
        }
        if (parsed.MaximumTurns is <= 0) {
            throw Refusal(
                context: context,
                detail: "maximumTurns must be positive when set."
            );
        }
        if (parsed.MaximumIterationsPerRequest <= 0) {
            throw Refusal(
                context: context,
                detail: "maximumIterationsPerRequest must be positive."
            );
        }
        if (context.Principal.Kind is not (PrincipalKind.Seat or PrincipalKind.Addon or PrincipalKind.Peer)) {
            throw Refusal(
                context: context,
                detail: $"principal '{context.Principal.Describe()}' must be a seat, addon, or peer: a participant acts through the world's grants, and the console holds every grant."
            );
        }
        ChatClientProvider provider;

        try {
            provider = ChatClientProvider.Select(
                extensions: context.Extensions,
                name: parsed.Provider
            );
        } catch (ArgumentException error) {
            throw Refusal(
                context: context,
                detail: error.Message
            );
        }
        var providerName = (parsed.Provider ?? context.Extensions.Contributions<ChatClientProvider>()[0].Key);
        IChatClient chatClient;

        try {
            chatClient = provider.Create(arg: ((parsed.ProviderSettings is { } providerSettings)
                ? providerSettings
                : EmptySettings()));
        } catch (Exception error) when ((error is ArgumentException or JsonException)) {
            throw Refusal(
                context: context,
                detail: $"provider '{providerName}' settings are invalid: {error.Message}"
            );
        }
        try {
            return new WorldAgentParticipant(
                chatClient: chatClient,
                context: context,
                provider: providerName,
                settings: parsed
            );
        } catch {
            chatClient.Dispose();
            throw;
        }
    }

    private static JsonElement EmptySettings() {
        using var document = JsonDocument.Parse(json: "{}");

        return document.RootElement.Clone();
    }
    private static string Bounded(string text) => ((text.Length <= MaximumFailureCharacters)
        ? text
        : text[..MaximumFailureCharacters]
    );
    private async Task RunAsync(CancellationToken cancellationToken) {
        Volatile.Write(
            location: ref m_state,
            value: 1
        );
        try {
            var session = await m_agent.CreateSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            var message = m_objective;

            while (!cancellationToken.IsCancellationRequested) {
                try {
                    _ = await m_agent.RunAsync(
                        cancellationToken: cancellationToken,
                        message: message,
                        session: session
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    Volatile.Write(
                        location: ref m_lastFailure,
                        value: null
                    );
                } catch (Exception error) when (!cancellationToken.IsCancellationRequested) {
                    Volatile.Write(
                        location: ref m_lastFailure,
                        value: Bounded(text: $"{error.GetType().Name}: {error.Message}")
                    );
                }
                if ((Interlocked.Increment(location: ref m_turns) >= m_maximumTurns) || cancellationToken.IsCancellationRequested) { break; }
                await Task.Delay(
                    cancellationToken: cancellationToken,
                    delay: m_turnInterval,
                    timeProvider: m_clock
                ).ConfigureAwait(continueOnCapturedContext: false);
                message = Continuation;
            }
        } catch (Exception) when (cancellationToken.IsCancellationRequested) {
            // Stopping: whatever the in-flight turn threw — a cancellation, a queued operation the closed mailbox
            // refused, a provider failure — ends the loop without surfacing through disposal.
        } catch (Exception error) {
            Volatile.Write(
                location: ref m_lastFailure,
                value: Bounded(text: $"{error.GetType().Name}: {error.Message}")
            );
        } finally {
            Volatile.Write(
                location: ref m_state,
                value: 2
            );
        }
    }

    /// <inheritdoc/>
    public string Describe() => $"type={Type} principal={m_bridge.Principal.Describe()} body={m_bridge.BodyIndex} provider={m_provider} state={(Volatile.Read(location: ref m_state) switch {
        0 => "idle",
        1 => "running",
        _ => "stopped",
    })} turns={Turns} failure={(LastFailure ?? "none")}";
    /// <summary>Stops the worker, refuses its queued world operations, waits for it to finish, and disposes the model
    /// client. Later calls do nothing.</summary>
    /// <returns>A task that completes once the worker has stopped.</returns>
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) != 0) {
            return;
        }
        await m_stop.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
        m_mailbox.Dispose();
        await m_loop.ConfigureAwait(continueOnCapturedContext: false);
        m_chatClient.Dispose();
        m_stop.Dispose();
    }
    /// <inheritdoc/>
    public void Pump(ulong completedTick) => m_mailbox.Drain();
    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">The participant has been disposed.</exception>
    public void Start() {
        ObjectDisposedException.ThrowIf(
            condition: (Volatile.Read(location: ref m_disposed) != 0),
            instance: this
        );
        if (Interlocked.Exchange(
            location1: ref m_started,
            value: 1
        ) != 0) {
            return;
        }
        m_loop = Task.Run(
            cancellationToken: CancellationToken.None,
            function: () => RunAsync(cancellationToken: m_stop.Token)
        );
    }
}

/// <summary>The <c>agent.harness</c> participant's settings.</summary>
/// <param name="Objective">What the participant works toward; its first turn's message.</param>
/// <param name="Provider">The installed chat client provider name, or <see langword="null"/> for the one installed.</param>
/// <param name="ProviderSettings">The provider's own settings object, or <see langword="null"/> for an empty object.</param>
/// <param name="Instructions">Optional role instructions added to the Harness's own.</param>
/// <param name="Approval"><c>refuse</c> offers the model no action tools, so the participant only observes;
/// <c>allow</c> offers them without a Harness approval step, making this configuration the consent. Puck's grants
/// decide every action either way.</param>
/// <param name="TurnSeconds">Seconds, on the host clock, between the end of one turn and the start of the next.</param>
/// <param name="MaximumTurns">Stops the participant after this many turns, or <see langword="null"/> to run until the host stops.</param>
/// <param name="MaximumIterationsPerRequest">The Harness's model/tool iteration limit per turn.</param>
/// <param name="Planning">Whether the Harness exposes its todo provider.</param>
/// <param name="Telemetry">Whether the Harness emits OpenTelemetry activities.</param>
internal sealed record WorldAgentParticipantSettings(
    string Objective,
    string? Provider = null,
    JsonElement? ProviderSettings = null,
    string? Instructions = null,
    string Approval = "refuse",
    double TurnSeconds = 10d,
    int? MaximumTurns = null,
    int MaximumIterationsPerRequest = 12,
    bool Planning = true,
    bool Telemetry = true
);
[JsonSerializable(typeof(WorldAgentParticipantSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
internal sealed partial class WorldAgentParticipantJson : JsonSerializerContext;
