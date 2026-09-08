namespace Puck.World.Server;

/// <summary>How an extension's authoritative contributions are reproduced.</summary>
public enum WorldExtensionReplayPolicy {
    /// <summary>Record contributions at the authority door; never execute the provider during replay.</summary>
    Recorded,
    /// <summary>Re-execute pinned code with identical inputs under a deterministic runtime.</summary>
    Recomputed,
    /// <summary>No authoritative contributions; local output is outside the state replay guarantee.</summary>
    PresentationOnly,
}

/// <summary>The common runtime lifetime and replay contract, independent of language or hosting mechanism.</summary>
/// <remarks>Runtime-specific interfaces add their own execution methods. This contract grants no authority and
/// does not make arbitrary in-process code a sandbox. External effects require a durable operation journal.</remarks>
public interface IWorldExtensionRuntime : IDisposable {
    /// <summary>Gets the contribution strategy this runtime implements.</summary>
    WorldExtensionReplayPolicy ReplayPolicy { get; }
}
