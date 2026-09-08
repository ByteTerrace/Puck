using Puck.World.Protocol;

namespace Puck.World.Agents;

/// <summary>The body-scoped read-backs exposed to an autonomous world participant.</summary>
public enum WorldAgentObservationKind : byte {
    /// <summary>The body's authoritative position and orientation.</summary>
    Pose,

    /// <summary>The body's resolved channel contributions and values.</summary>
    Channels,

    /// <summary>The body's named action-state registers.</summary>
    State,

    /// <summary>The body's target registers and latest designation refusal.</summary>
    Targets,

    /// <summary>The body's grounded and contact witnesses.</summary>
    Contacts,

    /// <summary>The body's live property set.</summary>
    Properties,
}

/// <summary>An authoritative body read-back suitable for returning from an AI tool.</summary>
/// <param name="Kind">The requested observation.</param>
/// <param name="BodyIndex">The observed 0-based body index.</param>
/// <param name="Text">The server-composed, deterministic description.</param>
/// <param name="Refused">Whether authority refused the read.</param>
public readonly record struct WorldAgentObservation(
    WorldAgentObservationKind Kind,
    int BodyIndex,
    string Text,
    bool Refused
);

/// <summary>One channel the live world exposes to the controlled body.</summary>
/// <param name="Name">The authored channel name.</param>
/// <param name="Ordinal">The deterministic channel ordinal.</param>
/// <param name="Shape">The channel's accepted value shape.</param>
/// <param name="IsMotionRole">Whether the channel drives an engine motion role.</param>
public readonly record struct WorldAgentChannel(
    string Name,
    int Ordinal,
    ChannelShape Shape,
    bool IsMotionRole
);

/// <summary>The bridge's current body-scoped capability and channel view.</summary>
/// <param name="Principal">The identity every read and action is attributed to.</param>
/// <param name="BodyIndex">The controlled body.</param>
/// <param name="CanObserve">Whether the principal currently holds Observe over the body.</param>
/// <param name="CanDrive">Whether the principal currently holds Drive over the body.</param>
/// <param name="Channels">The current live world's declared channels.</param>
/// <param name="AuthorityText">Authoritative grant explanations for the two capability checks.</param>
public sealed record WorldAgentAffordances(
    string Principal,
    int BodyIndex,
    bool CanObserve,
    bool CanDrive,
    IReadOnlyList<WorldAgentChannel> Channels,
    IReadOnlyList<string> AuthorityText
);

/// <summary>An honest receipt for an action submitted to Puck authority.</summary>
/// <param name="Action">The submitted action kind.</param>
/// <param name="BodyIndex">The target body.</param>
/// <param name="Correlated">Whether the link minted a local correlation id. A false value is deliberately not an
/// authorization verdict: a link may mint the envelope remotely, or it may have refused it before authority.</param>
/// <param name="CorrelationId">The local protocol correlation id, or zero when the link minted no local coordinate.</param>
/// <param name="Message">A caller-facing explanation that does not confuse submission with authoritative acceptance.</param>
public readonly record struct WorldAgentActionReceipt(
    string Action,
    int BodyIndex,
    bool Correlated,
    long CorrelationId,
    string Message
);
