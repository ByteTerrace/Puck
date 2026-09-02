using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Agents;

/// <summary>A provider-neutral, principal-scoped bridge between an autonomous participant and Puck's authoritative
/// world protocol. It exposes high-level body observations and actions while keeping grants as the hard authorization
/// boundary and keeping model execution outside the simulation tick.</summary>
public sealed class WorldAgentBridge {
    private readonly Func<WorldChannelTable> m_channels;
    private readonly IWorldAgentDispatcher m_dispatcher;
    private readonly IPrincipalServerLink m_link;

    /// <summary>Initializes a bridge over one acting principal and one controlled body.</summary>
    /// <param name="link">The principal-aware in-process server link.</param>
    /// <param name="dispatcher">Moves short link and live-definition operations onto the transport's required
    /// thread. Use the host-registered <see cref="WorldAgentMailbox"/> with Puck's loopback transport.</param>
    /// <param name="principal">The identity stamped on every query and command.</param>
    /// <param name="bodyIndex">The 0-based controlled body index.</param>
    /// <param name="channels">Returns the current live world's compiled channel table. It is evaluated for every
    /// affordance or action so a document reload cannot leave the agent using stale ordinals.</param>
    /// <exception cref="ArgumentNullException"><paramref name="link"/>, <paramref name="dispatcher"/>, or
    /// <paramref name="channels"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bodyIndex"/> is outside world capacity, or
    /// <paramref name="principal"/> is not an ingress-capable actor.</exception>
    public WorldAgentBridge(
        IPrincipalServerLink link,
        IWorldAgentDispatcher dispatcher,
        WorldPrincipal principal,
        int bodyIndex,
        Func<WorldChannelTable> channels
    ) {
        ArgumentNullException.ThrowIfNull(argument: link);
        ArgumentNullException.ThrowIfNull(argument: dispatcher);
        ArgumentNullException.ThrowIfNull(argument: channels);
        ArgumentOutOfRangeException.ThrowIfNegative(value: bodyIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: bodyIndex, other: WorldBodiesLimits.CapacityCeiling);
        if (principal.Kind is not (PrincipalKind.Seat or PrincipalKind.Console or PrincipalKind.Addon or PrincipalKind.Peer)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(principal),
                actualValue: principal,
                message: "The agent principal must be an ingress-capable seat, console, addon, or peer."
            );
        }

        m_channels = channels;
        m_dispatcher = dispatcher;
        m_link = link;
        BodyIndex = bodyIndex;
        Principal = principal;
    }

    /// <summary>Gets the 0-based body this bridge controls.</summary>
    public int BodyIndex { get; }

    /// <summary>Gets the identity stamped on every bridge operation.</summary>
    public WorldPrincipal Principal { get; }

    /// <summary>Reads one body-scoped authoritative observation.</summary>
    /// <param name="kind">The observation to read.</param>
    /// <param name="cancellationToken">Cancels waiting for a link completion.</param>
    /// <returns>The server-composed answer and refusal verdict.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unknown.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before the
    /// link completes.</exception>
    public async ValueTask<WorldAgentObservation> ObserveAsync(
        WorldAgentObservationKind kind,
        CancellationToken cancellationToken = default
    ) {
        WorldQuery query = kind switch {
            WorldAgentObservationKind.Pose => new WorldQuery.PlayerWhere(Index: BodyIndex),
            WorldAgentObservationKind.Channels => new WorldQuery.PlayerChannels(Index: BodyIndex),
            WorldAgentObservationKind.State => new WorldQuery.PlayerState(Index: BodyIndex),
            WorldAgentObservationKind.Targets => new WorldQuery.PlayerTargets(Index: BodyIndex),
            WorldAgentObservationKind.Contacts => new WorldQuery.Contacts(Index: (BodyIndex + 1)),
            WorldAgentObservationKind.Properties => new WorldQuery.Properties(BodyIndex: BodyIndex),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown world-agent observation kind."),
        };
        var answer = await QueryAsync(query, cancellationToken).ConfigureAwait(false);

        return new WorldAgentObservation(
            BodyIndex: BodyIndex,
            Kind: kind,
            Refused: answer.Refused,
            Text: answer.Text
        );
    }

    /// <summary>Reads current Observe/Drive grants and the live channel vocabulary for this body.</summary>
    /// <param name="cancellationToken">Cancels waiting for link completions.</param>
    /// <returns>The body-scoped affordance snapshot.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before a link
    /// completion.</exception>
    /// <exception cref="InvalidOperationException">The live channel source returns null.</exception>
    public async ValueTask<WorldAgentAffordances> GetAffordancesAsync(CancellationToken cancellationToken = default) {
        var subject = GrantSubject.Body(index: BodyIndex);
        var observe = await QueryAsync(
            query: new WorldQuery.GrantAllows(
                Capability: WorldCapability.Observe,
                Principal: Principal,
                Subject: subject
            ),
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        var drive = await QueryAsync(
            query: new WorldQuery.GrantAllows(
                Capability: WorldCapability.Drive,
                Principal: Principal,
                Subject: subject
            ),
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        var channels = await m_dispatcher.InvokeAsync(
            operation: DescribeChannels,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        return new WorldAgentAffordances(
            AuthorityText: [observe.Text, drive.Text],
            BodyIndex: BodyIndex,
            CanDrive: VerdictAllows(answer: drive),
            CanObserve: VerdictAllows(answer: observe),
            Channels: channels,
            Principal: Principal.Describe()
        );
    }

    /// <summary>Submits a timed six-axis motion segment using the live world's declared motion roles.</summary>
    /// <param name="forward">MoveAdvance in fixed-point input units.</param>
    /// <param name="strafe">MoveStrafe in fixed-point input units.</param>
    /// <param name="up">MoveUp in fixed-point input units.</param>
    /// <param name="yaw">Turn in fixed-point input units.</param>
    /// <param name="pitch">Pitch in fixed-point input units.</param>
    /// <param name="roll">Roll in fixed-point input units.</param>
    /// <param name="seconds">Positive simulation duration.</param>
    /// <param name="cancellationToken">Cancels the action while it is waiting in the dispatcher.</param>
    /// <returns>A submission receipt. Read back the body to learn the resulting authoritative state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite, or <paramref name="seconds"/> is not a
    /// positive finite <see cref="float"/> value.</exception>
    /// <exception cref="InvalidOperationException">The live channel source returns null.</exception>
    public ValueTask<WorldAgentActionReceipt> MoveAsync(
        double forward,
        double strafe,
        double up,
        double yaw,
        double pitch,
        double roll,
        double seconds,
        CancellationToken cancellationToken = default
    ) {
        RequireFinite(value: forward, parameterName: nameof(forward));
        RequireFinite(value: strafe, parameterName: nameof(strafe));
        RequireFinite(value: up, parameterName: nameof(up));
        RequireFinite(value: yaw, parameterName: nameof(yaw));
        RequireFinite(value: pitch, parameterName: nameof(pitch));
        RequireFinite(value: roll, parameterName: nameof(roll));
        var duration = RequirePositiveFiniteFloat(value: seconds, parameterName: nameof(seconds));
        return m_dispatcher.InvokeAsync(
            operation: () => {
                var roles = CurrentChannels().RoleOrdinals;

                return Submit(
                    action: "move",
                    command: new WorldCommand.EnqueueSegment(
                        EntityIndex: BodyIndex,
                        Intent: roles.Intent(
                            moveAdvance: FixedQ4816.FromDouble(value: forward),
                            moveStrafe: FixedQ4816.FromDouble(value: strafe),
                            moveUp: FixedQ4816.FromDouble(value: up),
                            pitch: FixedQ4816.FromDouble(value: pitch),
                            roll: FixedQ4816.FromDouble(value: roll),
                            turn: FixedQ4816.FromDouble(value: yaw)
                        ),
                        Principal: Principal,
                        Seconds: duration
                    )
                );
            },
            cancellationToken: cancellationToken
        );
    }

    /// <summary>Submits a named channel press using the current live channel table.</summary>
    /// <param name="channel">The authored channel name.</param>
    /// <param name="value">The raw channel value; authority applies the authored shape and grant ceilings.</param>
    /// <param name="holdSeconds">A positive simulation duration, or null for one host step.</param>
    /// <param name="cancellationToken">Cancels the action while it is waiting in the dispatcher.</param>
    /// <returns>A submission receipt. Read back channels/state to learn the resulting authoritative state.</returns>
    /// <exception cref="ArgumentException"><paramref name="channel"/> is blank or undeclared.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not finite, or
    /// <paramref name="holdSeconds"/> is not a positive finite <see cref="float"/> value.</exception>
    /// <exception cref="InvalidOperationException">The live channel source returns null.</exception>
    public ValueTask<WorldAgentActionReceipt> PressAsync(
        string channel,
        double value = 1d,
        double? holdSeconds = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: channel);
        RequireFinite(value: value, parameterName: nameof(value));
        var duration = ((holdSeconds is { } seconds)
            ? RequirePositiveFiniteFloat(value: seconds, parameterName: nameof(holdSeconds))
            : (float?)null
        );

        return m_dispatcher.InvokeAsync(
            operation: () => {
                var channels = CurrentChannels();
                if (!channels.TryGetOrdinal(name: channel, ordinal: out var ordinal)) {
                    throw new ArgumentException(message: $"The live world declares no channel named '{channel}'.", paramName: nameof(channel));
                }

                return Submit(
                    action: $"press:{channel}",
                    command: new WorldCommand.PressChannel(
                        ChannelOrdinal: ordinal,
                        EntityIndex: BodyIndex,
                        HoldSeconds: duration,
                        Principal: Principal,
                        Value: FixedQ4816.FromDouble(value: value)
                    )
                );
            },
            cancellationToken: cancellationToken
        );
    }

    /// <summary>Clears the body's movement tape and held channels.</summary>
    /// <param name="cancellationToken">Cancels the action while it is waiting in the dispatcher.</param>
    /// <returns>A submission receipt.</returns>
    public ValueTask<WorldAgentActionReceipt> StopAsync(CancellationToken cancellationToken = default) =>
        m_dispatcher.InvokeAsync(
            operation: () => Submit(
                action: "stop",
                command: new WorldCommand.Stop(
                    EntityIndex: BodyIndex,
                    Principal: Principal
                )
            ),
            cancellationToken: cancellationToken
        );

    private static bool VerdictAllows(QueryAnswer answer) => !answer.Refused &&
        (answer.Payload is GrantVerdict verdict) &&
        verdict.IsAllowed;

    private static void RequireFinite(double value, string parameterName) {
        if (!double.IsFinite(value)) {
            throw new ArgumentOutOfRangeException(parameterName, value, "Agent action values must be finite.");
        }
    }

    private static float RequirePositiveFiniteFloat(double value, string parameterName) {
        if (!double.IsFinite(value) || (value <= 0d) || (value > float.MaxValue)) {
            throw new ArgumentOutOfRangeException(parameterName, value, "Agent action durations must be positive finite float values.");
        }

        return (float)value;
    }

    private WorldChannelTable CurrentChannels() => (m_channels() ?? throw new InvalidOperationException(
        message: "The live world channel source returned null."
    ));

    private IReadOnlyList<WorldAgentChannel> DescribeChannels() {
        var channels = CurrentChannels();
        var result = new WorldAgentChannel[channels.ChannelCount];

        for (var ordinal = 0; ordinal < result.Length; ordinal++) {
            result[ordinal] = new WorldAgentChannel(
                IsMotionRole: channels.IsRole(ordinal: ordinal),
                Name: channels.Name(ordinal: ordinal)!,
                Ordinal: ordinal,
                Shape: channels.Shape(ordinal: ordinal)
            );
        }

        return result;
    }

    private async ValueTask<QueryAnswer> QueryAsync(WorldQuery query, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<QueryAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            callback: static state => {
                var (source, token) = ((TaskCompletionSource<QueryAnswer>, CancellationToken))state!;
                source.TrySetCanceled(cancellationToken: token);
            },
            state: (completion, cancellationToken)
        );

        _ = await m_dispatcher.InvokeAsync(
            operation: () => {
                m_link.Query(
                    completion: answer => completion.TrySetResult(result: answer),
                    principal: Principal,
                    query: query
                );

                return true;
            },
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        return await completion.Task.ConfigureAwait(false);
    }

    private WorldAgentActionReceipt Submit(string action, WorldCommand command) {
        var correlationId = m_link.SubmitEnvelope(
            payload: new WorldSubmissionPayload.Command(Value: command),
            principal: Principal
        );
        var correlated = (correlationId != 0L);

        return new WorldAgentActionReceipt(
            Action: action,
            BodyIndex: BodyIndex,
            Correlated: correlated,
            CorrelationId: correlationId,
            Message: (correlated
                ? "Submitted to Puck authority. This receipt does not claim the action was authorized or applied; observe the body to verify the outcome."
                : "No local correlation was minted. The link may have minted the envelope remotely or refused it before authority; observe the body to verify the outcome.")
        );
    }
}
