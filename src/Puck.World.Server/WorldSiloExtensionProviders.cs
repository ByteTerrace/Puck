using System.Text.Json;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Supplies live host retirement deadlines independently of simulation and replay.</summary>
/// <remarks>The composition root owns this observer. Dispose it after observation has stopped.</remarks>
public interface IWorldHostRetirementObserver : IDisposable {
    /// <summary>Observes until cancellation or one retirement, awaiting the host callback before completion.</summary>
    /// <param name="retire">Stops admission and durably saves hosted worlds by the supplied UTC deadline. Failures must propagate.</param>
    /// <param name="cancellationToken">Ends observation when the host stops.</param>
    /// <returns>The observation lifetime; completing successfully never implies a failed retirement succeeded.</returns>
    Task RunAsync(Func<DateTimeOffset, CancellationToken, Task> retire, CancellationToken cancellationToken);
}
/// <summary>A persistence provider an installed extension contributes, selected by its key.</summary>
/// <param name="Type">The contribution key a silo document selects.</param>
/// <param name="Create">Validates provider-owned settings and returns the silo's storage target without network I/O.</param>
public sealed record WorldSiloStorageProvider(string Type, Func<JsonElement, ObjectStorageTarget> Create);
/// <summary>A retirement observer provider an installed extension contributes, selected by its key.</summary>
/// <param name="Type">The contribution key a silo document selects.</param>
/// <param name="Create">Validates provider-owned settings and creates an owned observer without starting observation.
/// The observer's polling, deadlines and instant reads run on the supplied host clock.</param>
public sealed record WorldSiloRetirementProvider(string Type, Func<JsonElement, TimeProvider, IWorldHostRetirementObserver> Create);
/// <summary>An installed connection-identity extension. Credentials remain outside simulation.</summary>
/// <param name="Type">The contribution key a silo row or client authentication configuration selects.</param>
/// <param name="Server">Wraps a world's existing federation authenticator with the selected admission policy; the
/// policy's deadlines and instant reads run on the supplied host clock.</param>
/// <param name="Client">Resolves a client authenticator and the identity its credential represents; its deadlines and
/// instant reads run on the supplied host clock.</param>
public sealed record WorldAuthenticationProvider(string Type,
    Func<JsonElement, Puck.Networking.IAuthenticator, TimeProvider, Puck.Networking.IAuthenticator> Server,
    Func<JsonElement, TimeProvider, (Puck.Networking.IAuthenticator Authenticator, string Subject)> Client);
