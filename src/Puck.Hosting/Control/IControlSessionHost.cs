namespace Puck.Hosting;

/// <summary>A host's explicitly addressable Console targets. Transports own authentication and authorization before calling this interface.</summary>
public interface IControlSessionHost {
    /// <summary>Checks whether the exact target currently accepts ingress, without opening a session.</summary>
    /// <param name="target">The host-selected target identifier.</param>
    /// <returns>Whether the target is ready.</returns>
    bool IsReady(string target);
    /// <summary>Opens independently ordered Console ingress. Host retirement must close the returned session.</summary>
    /// <param name="target">The immutable target identifier.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>A session owned and disposed by the caller.</returns>
    ValueTask<IControlSession> AttachAsync(string target, CancellationToken cancellationToken);
}
