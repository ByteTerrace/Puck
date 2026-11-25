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
/// <summary>A storage provider an installed extension contributes, selected by its key: the one way a host reaches a
/// store, so no host names a provider's target type.</summary>
/// <param name="Type">The contribution key a silo document's <c>store</c> or a World's storage configuration selects.</param>
/// <param name="Create">Validates provider-owned settings and returns the storage target without network I/O.</param>
public sealed record WorldStorageProvider(string Type, Func<JsonElement, ObjectStorageTarget> Create);
/// <summary>A retirement observer provider an installed extension contributes, selected by its key.</summary>
/// <param name="Type">The contribution key a silo document selects.</param>
/// <param name="Create">Validates provider-owned settings and creates an owned observer without starting observation.
/// The observer's polling, deadlines and instant reads run on the supplied host clock.</param>
public sealed record WorldSiloRetirementProvider(string Type, Func<JsonElement, TimeProvider, IWorldHostRetirementObserver> Create);
/// <summary>An installed sign-in extension: how a player proves who they are to an issuer
/// (<see cref="WorldAttestationIssuer"/>), which then vouches for the player's own per-run key. A sign-in credential
/// reaches only the issuer; no world verifies one, and none enters a document, checkpoint or simulation.</summary>
/// <param name="Type">The contribution key an issuer configuration's <c>signIn</c> or a client's authentication
/// configuration selects.</param>
/// <param name="Verifier">Validates provider-owned settings and creates the issuer-side verifier; its deadlines and
/// instant reads run on the supplied host clock.</param>
/// <param name="Client">Validates provider-owned settings and creates the client-side credential source; its deadlines
/// and instant reads run on the supplied host clock.</param>
public sealed record WorldSignInProvider(string Type,
    Func<JsonElement, TimeProvider, IWorldSignInVerifier> Verifier,
    Func<JsonElement, TimeProvider, IWorldSignInCredential> Client);
/// <summary>The subject a sign-in established.</summary>
/// <param name="Subject">The provider's stable identifier for the person: one to 128 ASCII letters, digits,
/// <c>.</c>, <c>_</c> or <c>-</c>, so it reads as one segment of a federation namespace.</param>
public sealed record WorldSignIn(string Subject) {
    /// <summary>Returns whether <paramref name="subject"/> is a well-formed sign-in subject.</summary>
    /// <param name="subject">The candidate subject.</param>
    /// <returns><see langword="true"/> when it is one namespace segment of the permitted characters.</returns>
    public static bool IsWellFormed(string? subject) => (subject is { Length: > 0 and <= 128 }) &&
        subject.All(predicate: static c => (char.IsAsciiLetterOrDigit(c: c) || (c is '.' or '_' or '-')));
}
/// <summary>The issuer side of a sign-in provider.</summary>
public interface IWorldSignInVerifier {
    /// <summary>Validates a presented credential.</summary>
    /// <param name="credential">The credential bytes the client's provider presented.</param>
    /// <param name="challenge">The issuance exchange's transport-bound challenge, which a provider that signs may
    /// require its credential to cover.</param>
    /// <param name="cancellationToken">Cancels issuer metadata retrieval.</param>
    /// <returns>The established subject, or <see langword="null"/> when the credential is refused.</returns>
    ValueTask<WorldSignIn?> VerifyAsync(ReadOnlyMemory<byte> credential, ReadOnlyMemory<byte> challenge, CancellationToken cancellationToken);
}
/// <summary>The client side of a sign-in provider: acquires the credential from the sign-in the provider documents.</summary>
public interface IWorldSignInCredential {
    /// <summary>Acquires and caches what the credential needs before an issuance exchange opens, so the exchange
    /// itself never waits on the sign-in while the issuer's handshake deadline runs. A provider whose credential is
    /// only a signature over the challenge has nothing to acquire.</summary>
    /// <param name="cancellationToken">Cancels credential acquisition.</param>
    /// <exception cref="InvalidOperationException">The provider could not obtain a credential.</exception>
    void Acquire(CancellationToken cancellationToken);
    /// <summary>Presents a credential for one issuance exchange.</summary>
    /// <param name="challenge">The issuance exchange's transport-bound challenge.</param>
    /// <param name="cancellationToken">Cancels credential acquisition.</param>
    /// <returns>The credential bytes.</returns>
    /// <exception cref="InvalidOperationException">The provider could not obtain a credential.</exception>
    byte[] Present(ReadOnlySpan<byte> challenge, CancellationToken cancellationToken);
}
