using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Puck.World.Agents.Harness;

/// <summary>Builds Microsoft's Agent Framework Harness around a constrained set of typed Puck world tools.</summary>
public static class WorldAgentHarness {
    private const string ActingInstructions = """
        Observe before acting. Prefer short, bounded actions, then observe again before choosing the next action.
        A submission receipt proves only that Puck received an envelope; it never proves the action was authorized or applied.
        Never report an action as applied until a later observation supports that conclusion.
        """;

    private static string InstructionsFor(WorldAgentBridge bridge, WorldAgentActions actions) {
        var identity = $"""
            You are an autonomous participant embodied in Puck body {bridge.BodyIndex} as principal {bridge.Principal.Describe()}.
            Puck is authoritative. Use only the provided puck_* tools to observe or affect the world.
            """;

        return actions switch {
            WorldAgentActions.None => $"""
                {identity}
                This deployment offers no action tools: you can observe the world but not change it.
                """,
            WorldAgentActions.RequireApproval => $"""
                {identity}
                {ActingInstructions}
                Harness approval is human consent to invoke a tool. Puck's grants are the independent authorization boundary and may still refuse it.
                """,
            WorldAgentActions.Unattended => $"""
                {identity}
                {ActingInstructions}
                """,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: actions,
                message: "Unknown world-agent action mode.",
                paramName: nameof(actions)
            ),
        };
    }

    /// <summary>Creates a Harness agent over an injected model client and an already scoped Puck bridge.</summary>
    /// <param name="chatClient">Any Microsoft.Extensions.AI-compatible model client.</param>
    /// <param name="bridge">The principal/body-scoped Puck bridge.</param>
    /// <param name="options">Harness composition options, or null for safe defaults.</param>
    /// <param name="loggerFactory">Optional Harness logger factory.</param>
    /// <param name="services">Optional services used to resolve Agent Framework dependencies.</param>
    /// <returns>A stateful Harness agent. Create and retain an <see cref="AgentSession"/> across turns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chatClient"/> or <paramref name="bridge"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The iteration limit is not positive, or the action mode is not a
    /// defined <see cref="WorldAgentActions"/> value.</exception>
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
        var instructions = InstructionsFor(
            actions: options.Actions,
            bridge: bridge
        );
        var tools = new WorldAgentTools(bridge: bridge).CreateFunctions(actions: options.Actions);

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
                DisableAgentSkillsProvider = (options.SkillsSource is null),
                DisableFileMemory = true,
                DisableOpenTelemetry = !options.EnableOpenTelemetry,
                DisableTodoProvider = !options.EnablePlanning,
                DisableWebSearch = true,
                HarnessInstructions = instructions,
                Id = options.Id,
                MaximumIterationsPerRequest = options.MaximumIterationsPerRequest,
                Name = options.Name,
            },
            services: services
        );
    }

    private sealed class WorldAgentTools(WorldAgentBridge bridge) {
        private readonly WorldAgentBridge m_bridge = bridge;

        private ValueTask<WorldAgentAffordances> GetAffordancesAsync(CancellationToken cancellationToken) =>
            m_bridge.GetAffordancesAsync(cancellationToken: cancellationToken);
        private ValueTask<WorldAgentActionReceipt> MoveAsync(
            [Description("MoveAdvance role value: a decimal in its channel's shape range.")] double forward,
            [Description("MoveStrafe role value: a decimal in its channel's shape range.")] double strafe,
            [Description("MoveUp role value: a decimal in its channel's shape range.")] double up,
            [Description("Turn role value: a decimal in its channel's shape range.")] double yaw,
            [Description("Pitch role value: a decimal in its channel's shape range.")] double pitch,
            [Description("Roll role value: a decimal in its channel's shape range.")] double roll,
            [Description("Positive finite simulation duration, in seconds.")] double seconds,
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
        private ValueTask<WorldAgentObservation> ObserveAsync(
            [Description("pose, channels, state, targets, contacts, or properties")]
            string aspect,
            CancellationToken cancellationToken
        ) {
            if (!Enum.TryParse<WorldAgentObservationKind>(
                ignoreCase: true,
                result: out var kind,
                value: aspect
            )) {
                throw new ArgumentException(
                    message: $"Unknown observation aspect '{aspect}'. Use pose, channels, state, targets, contacts, or properties.",
                    paramName: nameof(aspect)
                );
            }

            return m_bridge.ObserveAsync(
                cancellationToken: cancellationToken,
                kind: kind
            );
        }
        private ValueTask<WorldAgentActionReceipt> PressAsync(
            [Description("Exact authored channel name from puck_get_affordances.")] string channel,
            [Description("A decimal in the channel's shape range (Bipolar -1 to 1, Unipolar 0 to 1, Binary 0 or 1); authority applies the shape and grant ceilings.")] double value,
            [Description("Positive finite simulation duration in seconds, or null for one host step.")] double? holdSeconds,
            CancellationToken cancellationToken
        ) => m_bridge.PressAsync(
            cancellationToken: cancellationToken,
            channel: channel,
            holdSeconds: holdSeconds,
            value: value
        );
        private static AIFunction RequiringApprovalIfConfigured(AIFunction function, WorldAgentActions actions) => ((actions == WorldAgentActions.RequireApproval)
            ? new ApprovalRequiredAIFunction(innerFunction: function)
            : function
        );
        private ValueTask<WorldAgentActionReceipt> StopAsync(CancellationToken cancellationToken) =>
            m_bridge.StopAsync(cancellationToken: cancellationToken);

        public IList<AITool> CreateFunctions(WorldAgentActions actions) {
            var observe = AIFunctionFactory.Create(
                method: ((Func<string, CancellationToken, ValueTask<WorldAgentObservation>>)ObserveAsync),
                name: "puck_observe_body",
                description: "Read one authoritative aspect of this body as this principal: pose (position and orientation), channels (resolved channel contributions and values), state (named action-state registers), targets (target registers and the latest designation refusal), contacts (grounded and contact witnesses), or properties (the live property set). Returns the server-composed text and whether authority refused the read. The read runs at the simulation's next closed boundary. An unknown aspect is an error that lists the valid ones."
            );
            var affordances = AIFunctionFactory.Create(
                method: ((Func<CancellationToken, ValueTask<WorldAgentAffordances>>)GetAffordancesAsync),
                name: "puck_get_affordances",
                description: "Read the principal's current Observe/Drive grants and the live world's exact channel vocabulary. Call it before the first action and again after a grant change or world reload, because channel names and grants can change; the other tools refuse a channel name it does not list."
            );

            if (actions == WorldAgentActions.None) {
                return [observe, affordances];
            }
            var move = AIFunctionFactory.Create(
                method: ((Func<double, double, double, double, double, double, double, CancellationToken, ValueTask<WorldAgentActionReceipt>>)MoveAsync),
                name: "puck_move",
                description: "Submit a timed six-axis motion segment to the controlled body. Each axis drives the world's channel for one motion role (forward: MoveAdvance, strafe: MoveStrafe, up: MoveUp, yaw: Turn, pitch: Pitch, roll: Roll) and takes a decimal in that channel's shape range as puck_get_affordances reports it (Bipolar -1 to 1, Unipolar 0 to 1, Binary 0 or 1), quantized to Puck fixed-point on submission; zero leaves an axis idle. Authority applies the authored input shape and grant ceilings, so the applied motion can be smaller than requested or refused. seconds is a positive finite simulation duration. Returns a submission receipt only; observe the body's pose afterwards to learn what happened. Non-finite values are refused."
            );
            var press = AIFunctionFactory.Create(
                method: ((Func<string, double, double?, CancellationToken, ValueTask<WorldAgentActionReceipt>>)PressAsync),
                name: "puck_press_channel",
                description: "Submit a press of one exact channel name returned by puck_get_affordances. value is a decimal in that channel's shape range (Bipolar -1 to 1, Unipolar 0 to 1, Binary 0 or 1), quantized to Puck fixed-point on submission; authority applies the channel's authored shape and grant ceilings. holdSeconds is a positive finite simulation duration, or null for a single host step. Returns a submission receipt only; observe channels or state afterwards to learn the effect. A blank or undeclared channel, or a non-finite value, is refused."
            );
            var stop = AIFunctionFactory.Create(
                method: ((Func<CancellationToken, ValueTask<WorldAgentActionReceipt>>)StopAsync),
                name: "puck_stop",
                description: "Submit a stop for the controlled body: clears its queued movement segments and releases every held channel, ending a puck_move segment or held puck_press_channel before its duration runs out. Returns a submission receipt only; authority can still refuse it, so observe pose or channels to confirm."
            );

            return [
                observe,
                affordances,
                RequiringApprovalIfConfigured(
                    actions: actions,
                    function: move
                ),
                RequiringApprovalIfConfigured(
                    actions: actions,
                    function: press
                ),
                RequiringApprovalIfConfigured(
                    actions: actions,
                    function: stop
                ),
            ];
        }
    }
}
