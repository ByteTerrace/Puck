namespace Puck.Hosting;

/// <summary>A host's explicitly addressable control targets. Transports validate identity; the host authorizes it and stamps each returned session with its acting principal.</summary>
public interface IControlSessionHost {
    /// <summary>Checks whether the exact target currently accepts ingress, without opening a session.</summary>
    /// <param name="target">The host-selected target identifier.</param>
    /// <returns>Whether the target is ready.</returns>
    bool IsReady(string target);
    /// <summary>Opens independently ordered Console ingress. Host retirement must close the returned session.</summary>
    /// <param name="target">The immutable target identifier.</param>
    /// <param name="identity">The authenticated control identity presented to the host for attachment.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>A session owned and disposed by the caller.</returns>
    ValueTask<IControlSession> AttachAsync(string target, ControlIdentity identity, CancellationToken cancellationToken);
}
