using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Puck.World.Agents.Harness;

/// <summary>Builds Microsoft's Agent Framework Harness around a constrained set of typed Puck world tools.</summary>
public static class WorldAgentHarness {
    /// <summary>Creates a Harness agent over an injected model client and an already scoped Puck bridge.</summary>
    /// <param name="chatClient">Any Microsoft.Extensions.AI-compatible model client.</param>
    /// <param name="bridge">The principal/body-scoped Puck bridge.</param>
    /// <param name="options">Harness composition options, or null for safe defaults.</param>
    /// <param name="loggerFactory">Optional Harness logger factory.</param>
    /// <param name="services">Optional services used to resolve Agent Framework dependencies.</param>
    /// <returns>A stateful Harness agent. Create and retain an <see cref="AgentSession"/> across turns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chatClient"/> or <paramref name="bridge"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The iteration limit is not positive.</exception>
    public static HarnessAgent Create(
        IChatClient chatClient,
        WorldAgentBridge bridge,
        WorldAgentHarnessOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: chatClient);
        ArgumentNullException.ThrowIfNull(argument: bridge);
        options ??= new WorldAgentHarnessOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            value: options.MaximumIterationsPerRequest,
            paramName: nameof(options.MaximumIterationsPerRequest)
        );
        var tools = new WorldAgentTools(bridge: bridge).CreateFunctions(
            requireActionApproval: options.RequireActionApproval
        );

        return new HarnessAgent(
            chatClient: chatClient,
            loggerFactory: loggerFactory,
            options: new HarnessAgentOptions {
                ChatOptions = new ChatOptions {
                    Instructions = options.Instructions,
                    Tools = tools,
                },
                Description = $"An autonomous participant controlling body {bridge.BodyIndex} as {bridge.Principal.Describe()} in a Puck world.",
                AgentSkillsSource = options.SkillsSource,
                DisableAgentModeProvider = true,
                DisableAgentSkillsProvider = options.SkillsSource is null,
                DisableFileMemory = true,
                DisableOpenTelemetry = !options.EnableOpenTelemetry,
                DisableTodoProvider = !options.EnablePlanning,
                DisableWebSearch = true,
                HarnessInstructions = InstructionsFor(bridge: bridge),
                Id = options.Id,
                MaximumIterationsPerRequest = options.MaximumIterationsPerRequest,
                Name = options.Name,
            },
            services: services
        );
    }

    private static string InstructionsFor(WorldAgentBridge bridge) => $"""
        You are an autonomous participant embodied in Puck body {bridge.BodyIndex} as principal {bridge.Principal.Describe()}.
        Puck is authoritative. Use only the provided puck_* tools to observe or affect the world.
        Call puck_get_affordances before your first action and whenever a grant or world reload may have changed what you can do.
        Observe before acting. Prefer short, bounded actions, then observe again before choosing the next action.
        A submission receipt proves only that Puck received an envelope; it never proves the action was authorized or applied.
        Never report an action as applied until a later observation supports that conclusion.
        Harness approval is human consent to invoke a tool. Puck's grants are the independent authorization boundary and may still refuse it.
        Do not invent channel names. Use the exact channel vocabulary returned by puck_get_affordances.
        """;

    private sealed class WorldAgentTools(WorldAgentBridge bridge) {
        private readonly WorldAgentBridge m_bridge = bridge;

        public IList<AITool> CreateFunctions(bool requireActionApproval) {
            var observe = AIFunctionFactory.Create(
                method: (Func<string, CancellationToken, ValueTask<WorldAgentObservation>>)ObserveAsync,
                name: "puck_observe_body",
                description: "Read one authoritative aspect of this body: pose, channels, state, targets, contacts, or properties."
            );
            var affordances = AIFunctionFactory.Create(
                method: (Func<CancellationToken, ValueTask<WorldAgentAffordances>>)GetAffordancesAsync,
                name: "puck_get_affordances",
                description: "Read the principal's current Observe/Drive grants and the live world's exact channel vocabulary."
            );
            var move = AIFunctionFactory.Create(
                method: (Func<double, double, double, double, double, double, double, CancellationToken, ValueTask<WorldAgentActionReceipt>>)MoveAsync,
                name: "puck_move",
                description: "Submit a short, timed six-axis motion segment to the controlled body. Values are forward, strafe, up, yaw, pitch, roll, and positive simulation seconds."
            );
            var press = AIFunctionFactory.Create(
                method: (Func<string, double, double?, CancellationToken, ValueTask<WorldAgentActionReceipt>>)PressAsync,
                name: "puck_press_channel",
                description: "Submit a press of an exact channel name returned by puck_get_affordances, optionally for a positive simulation duration."
            );
            var stop = AIFunctionFactory.Create(
                method: (Func<CancellationToken, ValueTask<WorldAgentActionReceipt>>)StopAsync,
                name: "puck_stop",
                description: "Submit a command that clears the body's movement tape and releases every held channel."
            );

            return [
                observe,
                affordances,
                RequiringApprovalIfConfigured(function: move, required: requireActionApproval),
                RequiringApprovalIfConfigured(function: press, required: requireActionApproval),
                RequiringApprovalIfConfigured(function: stop, required: requireActionApproval),
            ];
        }

        [Description("Read one authoritative aspect of the controlled Puck body.")]
        private ValueTask<WorldAgentObservation> ObserveAsync(
            [Description("pose, channels, state, targets, contacts, or properties")]
            string aspect,
            CancellationToken cancellationToken
        ) {
            if (!Enum.TryParse<WorldAgentObservationKind>(
                value: aspect,
                ignoreCase: true,
                result: out var kind
            )) {
                throw new ArgumentException(
                    message: $"Unknown observation aspect '{aspect}'. Use pose, channels, state, targets, contacts, or properties.",
                    paramName: nameof(aspect)
                );
            }

            return m_bridge.ObserveAsync(kind: kind, cancellationToken: cancellationToken);
        }

        [Description("Read this body's live channels and the principal's Observe and Drive grants.")]
        private ValueTask<WorldAgentAffordances> GetAffordancesAsync(CancellationToken cancellationToken) =>
            m_bridge.GetAffordancesAsync(cancellationToken: cancellationToken);

        [Description("Submit a bounded six-axis motion segment.")]
        private ValueTask<WorldAgentActionReceipt> MoveAsync(
            [Description("Forward/backward value.")] double forward,
            [Description("Left/right strafe value.")] double strafe,
            [Description("Up/down value.")] double up,
            [Description("Yaw/turn value.")] double yaw,
            [Description("Pitch value.")] double pitch,
            [Description("Roll value.")] double roll,
            [Description("Positive simulation duration in seconds.")] double seconds,
            CancellationToken cancellationToken
        ) => m_bridge.MoveAsync(
            cancellationToken: cancellationToken,
            forward: forward,
            pitch: pitch,
            roll: roll,
            seconds: seconds,
            strafe: strafe,
            up: up,
            yaw: yaw
        );

        [Description("Submit a named channel press.")]
        private ValueTask<WorldAgentActionReceipt> PressAsync(
            [Description("Exact authored channel name from puck_get_affordances.")] string channel,
            [Description("Raw channel value.")] double value,
            [Description("Positive simulation duration, or null for one host step.")] double? holdSeconds,
            CancellationToken cancellationToken
        ) => m_bridge.PressAsync(
            cancellationToken: cancellationToken,
            channel: channel,
            holdSeconds: holdSeconds,
            value: value
        );

        [Description("Clear the controlled body's movement tape and held channels.")]
        private ValueTask<WorldAgentActionReceipt> StopAsync(CancellationToken cancellationToken) =>
            m_bridge.StopAsync(cancellationToken: cancellationToken);

        private static AIFunction RequiringApprovalIfConfigured(AIFunction function, bool required) => (required
            ? new ApprovalRequiredAIFunction(function)
            : function
        );
    }
}
