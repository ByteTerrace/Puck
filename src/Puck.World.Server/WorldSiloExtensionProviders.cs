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
/// <summary>An explicitly installed persistence extension, selected through the shared keyed registry.</summary>
/// <param name="Type">The extension key.</param>
/// <param name="Create">Validates provider-owned settings and returns the silo's storage target without network I/O.</param>
public sealed record WorldSiloStorageProvider(string Type, Func<JsonElement, ObjectStorageTarget> Create);
/// <summary>An explicitly installed retirement observer, selected through the shared keyed registry.</summary>
/// <param name="Type">The extension key.</param>
/// <param name="Create">Validates provider-owned settings and creates an owned observer without starting observation.</param>
public sealed record WorldSiloRetirementProvider(string Type, Func<JsonElement, IWorldHostRetirementObserver> Create);
/// <summary>An installed connection-identity extension. Credentials remain outside simulation.</summary>
/// <param name="Type">The extension key.</param>
/// <param name="Server">Wraps a world's existing federation authenticator with the selected admission policy.</param>
/// <param name="Client">Resolves a client authenticator and the identity its credential represents.</param>
public sealed record WorldAuthenticationProvider(string Type,
    Func<JsonElement, Puck.Networking.IAuthenticator, Puck.Networking.IAuthenticator> Server,
    Func<JsonElement, (Puck.Networking.IAuthenticator Authenticator, string Subject)> Client);
