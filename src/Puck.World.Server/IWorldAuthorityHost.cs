namespace Puck.World.Server;

/// <summary>Activation/deactivation for hosted world instances. The in-process implementation is the desktop's own
/// composition path; a hosted implementation is a scheduler's own activation callback — one row per identity, many
/// identities per host. Neither implementation is visible to the other, and this project names neither — both live
/// above this project's exact-equality closure (<c>build/Architecture.props</c>).</summary>
public interface IWorldAuthorityHost {
    /// <summary>Activates the world instance identified by <paramref name="identity"/>.</summary>
    /// <param name="identity">The world instance to activate.</param>
    /// <param name="ct">A token that cancels the activation.</param>
    /// <returns><see langword="true"/> once the instance is ready to receive submissions; <see langword="false"/>
    /// on a refused activation (a malformed identity, a failed load).</returns>
    Task<bool> ActivateAsync(WorldAuthorityIdentity identity, CancellationToken ct);
    /// <summary>Deactivates the world instance identified by <paramref name="identity"/>.</summary>
    /// <param name="identity">The world instance to deactivate.</param>
    /// <param name="ct">A token that cancels the deactivation.</param>
    Task DeactivateAsync(WorldAuthorityIdentity identity, CancellationToken ct);
}
