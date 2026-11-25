using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions;
using Puck.Attestation;
using Puck.Networking;
using Puck.Networking.Peers;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldFederationCodec {
    /// <summary>Binds an issuance exchange's challenge to both authenticated transport keys and the key being vouched
    /// for, so a proof of possession elicited on one connection, or for one key, is worthless on any other.</summary>
    /// <param name="challenge">The nonce the issuer sent for this exchange.</param>
    /// <param name="clientKeyHash">The authenticated connecting peer's transport key fingerprint.</param>
    /// <param name="serverKeyHash">The authenticated accepting peer's transport key fingerprint.</param>
    /// <param name="subjectPublicKeyInfo">The DER SubjectPublicKeyInfo of the key the client asks to have vouched for.</param>
    /// <returns>The transcript-bound challenge both the proof of possession and a signing sign-in cover.</returns>
    /// <exception cref="ArgumentException">The challenge is empty or longer than <see cref="MaxChallengeBytes"/>, or a
    /// transport key fingerprint is blank.</exception>
    public static byte[] BindIssuanceChallenge(ReadOnlySpan<byte> challenge, string clientKeyHash, string serverKeyHash, ReadOnlySpan<byte> subjectPublicKeyInfo) {
        if ((challenge.Length == 0) || (challenge.Length > MaxChallengeBytes)) {
            throw new ArgumentException(message: "issuance challenges must have a bounded, nonzero length");
        }
        return ChannelBinding.Bind(
            clientKeyHash: clientKeyHash,
            context: subjectPublicKeyInfo,
            label: "puck.world.issuance.v1",
            nonce: challenge,
            serverKeyHash: serverKeyHash
        );
    }
    /// <summary>Encodes an issuance request: the sign-in credential, the key to vouch for, and that key's signature
    /// over the bound challenge.</summary>
    /// <param name="credential">The sign-in provider's credential bytes.</param>
    /// <param name="subjectPublicKeyInfo">The DER SubjectPublicKeyInfo of the client's per-run key.</param>
    /// <param name="possession">The per-run key's signature over <see cref="BindIssuanceChallenge"/>.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeIssuance(ReadOnlySpan<byte> credential, ReadOnlySpan<byte> subjectPublicKeyInfo, ReadOnlySpan<byte> possession) {
        var writer = new WireWriter(capacity: (credential.Length + 256));

        writer.WriteBlock(value: credential);
        writer.WriteBlock(value: subjectPublicKeyInfo);
        writer.WriteBlock(value: possession);
        return writer.ToArray();
    }
    /// <summary>Decodes an issuance request.</summary>
    /// <param name="body">The untrusted leaf bytes.</param>
    /// <param name="credential">The sign-in credential.</param>
    /// <param name="subjectPublicKeyInfo">The key to vouch for.</param>
    /// <param name="possession">The key's signature over the bound challenge.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeIssuance(ReadOnlySpan<byte> body, out byte[] credential, out byte[] subjectPublicKeyInfo, out byte[] possession, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        credential = reader.ReadBlock(field: "sign-in credential", maxBytes: MaxProofBytes);
        subjectPublicKeyInfo = reader.ReadBlock(field: "subject key", maxBytes: AttestationResourceLimits.SubjectPublicKeyInfoBytes);
        possession = reader.ReadBlock(field: "proof of possession", maxBytes: AttestationResourceLimits.SignatureBytes);
        return reader.TryFinish(failure: out failure);
    }
    /// <summary>Encodes the issuer's answer: the root-to-issuing and issuing-to-subject bindings.</summary>
    /// <param name="rootBinding">The encoded root-to-issuing binding.</param>
    /// <param name="subjectBinding">The encoded issuing-to-subject binding.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeIssued(ReadOnlySpan<byte> rootBinding, ReadOnlySpan<byte> subjectBinding) {
        var writer = new WireWriter(capacity: (rootBinding.Length + subjectBinding.Length + 16));

        writer.WriteBlock(value: rootBinding);
        writer.WriteBlock(value: subjectBinding);
        return writer.ToArray();
    }
    /// <summary>Decodes the issuer's answer.</summary>
    /// <param name="body">The untrusted leaf bytes.</param>
    /// <param name="rootBinding">The encoded root-to-issuing binding.</param>
    /// <param name="subjectBinding">The encoded issuing-to-subject binding.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeIssued(ReadOnlySpan<byte> body, out byte[] rootBinding, out byte[] subjectBinding, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        rootBinding = reader.ReadBlock(field: "root binding", maxBytes: AttestationResourceLimits.AttestationBytes);
        subjectBinding = reader.ReadBlock(field: "subject binding", maxBytes: AttestationResourceLimits.AttestationBytes);
        return reader.TryFinish(failure: out failure);
    }
}
/// <summary>
/// A host's attestation issuer: the one place a player's sign-in is checked and turned into a vouched key. The root
/// key vouches for an issuing key this process generates and never stores; the issuing key vouches for each player's
/// own per-run key once the player has signed in through the configured provider and proved it holds that key. Worlds
/// the host runs trust the root (<see cref="Trust"/>) and verify a player's claims offline, never calling the
/// provider; a world's own <c>admission</c> rows still decide what an authenticated namespace is admitted as.
/// </summary>
/// <remarks>The issuer reads the host clock at each issuance boundary; nothing it does enters simulation state. A
/// subject binding never outlives the issuing binding above it, and the issuing key is replaced once the time left on
/// its binding is shorter than a subject binding's lifetime.</remarks>
public sealed class WorldAttestationIssuer : IDisposable {
    /// <summary>The window a fresh issuing key's binding signs: a week, inside
    /// <see cref="WorldAttestedAuthenticator.MaximumRootBindingAge"/>.</summary>
    public static readonly TimeSpan IssuingValidity = TimeSpan.FromDays(value: 7);

    private static readonly IAttestationCodec Codec = new CborAttestationCodec();

    private readonly TimeProvider m_clock;
    private readonly Lock m_gate = new();
    private readonly ECDsa m_root;
    private readonly long m_subjectValiditySeconds;
    private readonly IWorldSignInVerifier m_verifier;

    private (ECDsa Key, byte[] Spki, byte[] Binding, long NotAfter)? m_issuing;
    private BoundTrust? m_bound;

    private sealed record BoundTrust(IReadOnlyList<WorldAdmissionEntry>? Policy, IReadOnlyList<WorldAdmissionEntry> Rows);

    /// <summary>Initializes a new instance of the <see cref="WorldAttestationIssuer"/> class over its root key.</summary>
    /// <param name="root">The root signing key; owned by this instance and disposed with it.</param>
    /// <param name="verifier">The sign-in provider's issuer-side verifier.</param>
    /// <param name="subjectValidity">How long each player's key binding stays valid; at most
    /// <see cref="WorldIssuerConfiguration.MaximumSubjectValiditySeconds"/>.</param>
    /// <param name="clock">The host clock issuance windows are read from; <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="verifier"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="subjectValidity"/> is not positive or exceeds the verifier's ceiling.</exception>
    public WorldAttestationIssuer(ECDsa root, IWorldSignInVerifier verifier, TimeSpan subjectValidity, TimeProvider? clock = null) {
        ArgumentNullException.ThrowIfNull(argument: root);
        ArgumentNullException.ThrowIfNull(argument: verifier);
        ArgumentOutOfRangeException.ThrowIfLessThan(subjectValidity, TimeSpan.FromSeconds(value: 1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subjectValidity, WorldAttestedAuthenticator.MaximumSubjectBindingAge);

        m_root = root;
        m_verifier = verifier;
        m_subjectValiditySeconds = (long)subjectValidity.TotalSeconds;
        m_clock = (clock ?? TimeProvider.System);
        RootPublicKey = root.ExportSubjectPublicKeyInfo();
        Domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: RootPublicKey);
        Root = new WorldAdmissionEntry(
            Domain: Domain,
            Mode: WorldAdmissionTrustMode.Vouches,
            Grants: [],
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            PublicKey: Convert.ToBase64String(inArray: RootPublicKey)
        );
    }

    /// <summary>Gets the root key's fingerprint: the domain every namespace this issuer vouches for names.</summary>
    public string Domain { get; }
    /// <summary>Gets the trust row naming this issuer's root, the one a world's authenticator adds to its document's
    /// own key-bearing rows.</summary>
    public WorldAdmissionEntry Root { get; }
    /// <summary>Gets the root's DER SubjectPublicKeyInfo.</summary>
    public byte[] RootPublicKey { get; }

    /// <summary>Loads an issuer from its configuration, selecting the sign-in provider from the composed extensions.</summary>
    /// <param name="configuration">The validated issuer configuration.</param>
    /// <param name="extensions">The host's composed extensions.</param>
    /// <param name="clock">The host clock.</param>
    /// <returns>The issuer; the caller owns disposal.</returns>
    /// <exception cref="PuckExtensionException">The named sign-in provider is not installed.</exception>
    /// <exception cref="ArgumentException">The root key or the provider's settings are refused.</exception>
    /// <exception cref="IOException">The root key file cannot be read.</exception>
    /// <exception cref="CryptographicException">The root key file is not a PKCS#8 private key.</exception>
    public static WorldAttestationIssuer Load(WorldIssuerConfiguration configuration, Puck.Abstractions.PuckExtensionSet extensions, TimeProvider clock) {
        ArgumentNullException.ThrowIfNull(argument: configuration);
        ArgumentNullException.ThrowIfNull(argument: extensions);

        var verifier = extensions.Select<WorldSignInProvider>(
            key: configuration.SignIn.Type,
            purpose: "Issuer sign-in"
        ).Verifier(configuration.SignIn.Settings, clock);
        var root = AttestationKeys.ImportPkcs8PrivateKey(
            algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            pkcs8: File.ReadAllBytes(path: configuration.KeyFile)
        );

        try {
            return new WorldAttestationIssuer(
                clock: clock,
                root: root,
                subjectValidity: TimeSpan.FromSeconds(seconds: configuration.SubjectValiditySeconds),
                verifier: verifier
            );
        } catch {
            root.Dispose();
            throw;
        }
    }

    /// <summary>Returns a world's key-bearing trust rows with this issuer's root added: the rows its federation
    /// authenticator verifies against. The same list instance answers while <paramref name="policy"/> is the same
    /// instance, so a credential's trust check sees an unchanged policy as unchanged.</summary>
    /// <param name="policy">The world's current <c>admission</c> rows, or <see langword="null"/> for none.</param>
    /// <returns>The rows with <see cref="Root"/> appended.</returns>
    public IReadOnlyList<WorldAdmissionEntry> Trust(IReadOnlyList<WorldAdmissionEntry>? policy) {
        if ((Volatile.Read(location: ref m_bound) is { } cached) && ReferenceEquals(objA: cached.Policy, objB: policy)) {
            return cached.Rows;
        }
        IReadOnlyList<WorldAdmissionEntry> rows = [.. (policy ?? []), Root];

        Volatile.Write(location: ref m_bound, value: new BoundTrust(Policy: policy, Rows: rows));
        return rows;
    }
    /// <summary>Checks a sign-in and a key's proof of possession, and returns the bindings vouching for the key.</summary>
    /// <param name="boundChallenge">The exchange's transport-bound challenge (<see cref="WorldFederationCodec.BindIssuanceChallenge"/>).</param>
    /// <param name="credential">The sign-in credential.</param>
    /// <param name="subjectPublicKeyInfo">The key to vouch for.</param>
    /// <param name="possession">The key's signature over <paramref name="boundChallenge"/>.</param>
    /// <param name="cancellationToken">Cancels the provider's verification.</param>
    /// <returns>The encoded root-to-issuing and issuing-to-subject bindings, or the refusal's reason.</returns>
    public async ValueTask<(byte[] RootBinding, byte[] SubjectBinding)?> IssueAsync(ReadOnlyMemory<byte> boundChallenge, ReadOnlyMemory<byte> credential,
        ReadOnlyMemory<byte> subjectPublicKeyInfo, ReadOnlyMemory<byte> possession, CancellationToken cancellationToken) {
        // Possession first: it costs one verify and no provider round trip, and a key the caller does not hold is
        // never vouched for whoever signed in.
        if (!AttestationKeys.VerifySignature(
            algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            data: boundChallenge.Span,
            publicKeySubjectPublicKeyInfo: subjectPublicKeyInfo.Span,
            signature: possession.Span
        )) {
            return null;
        }
        if (await m_verifier.VerifyAsync(
            cancellationToken: cancellationToken,
            challenge: boundChallenge,
            credential: credential
        ).ConfigureAwait(continueOnCapturedContext: false) is not { } signIn || !WorldSignIn.IsWellFormed(subject: signIn.Subject)) {
            return null;
        }

        lock (m_gate) {
            var now = m_clock.GetUtcNow().ToUnixTimeSeconds();
            var issuing = CurrentIssuingKey(now: now);
            // Backdated like every claim, so a verifier whose clock trails this host's still finds the binding valid.
            var notBefore = (now - 60);
            var subjectBinding = AttestationSigner.SignKeyBinding(
                codec: Codec,
                domain: Domain,
                notAfter: Math.Min(val1: (now + m_subjectValiditySeconds), val2: issuing.NotAfter),
                notBefore: notBefore,
                signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
                signerKey: issuing.Key,
                targetId: KeyId.ForSubject(
                    algorithm: AttestationAlgorithms.EcdsaP256Sha256,
                    domain: Domain,
                    subject: signIn.Subject,
                    subjectPublicKeyInfo: subjectPublicKeyInfo.Span
                ),
                targetSubjectPublicKeyInfo: subjectPublicKeyInfo.ToArray()
            );

            return (issuing.Binding, Codec.EncodeAttestation(attestation: subjectBinding));
        }
    }
    // The issuing key in force, replaced once less than a subject binding's lifetime remains on its own binding, so
    // every subject binding gets its full window.
    private (ECDsa Key, byte[] Spki, byte[] Binding, long NotAfter) CurrentIssuingKey(long now) {
        if ((m_issuing is { } current) && ((current.NotAfter - now) >= m_subjectValiditySeconds)) {
            return current;
        }
        var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var notAfter = (now + (long)IssuingValidity.TotalSeconds);
        var binding = AttestationSigner.SignKeyBinding(
            codec: Codec,
            domain: Domain,
            notAfter: notAfter,
            notBefore: (now - 60),
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            signerKey: m_root,
            targetId: KeyId.ForIssuing(
                algorithm: AttestationAlgorithms.EcdsaP256Sha256,
                domain: Domain,
                subjectPublicKeyInfo: spki
            ),
            targetSubjectPublicKeyInfo: spki
        );

        m_issuing?.Key.Dispose();
        m_issuing = (key, spki, Codec.EncodeAttestation(attestation: binding), notAfter);
        return m_issuing.Value;
    }
    /// <inheritdoc/>
    public void Dispose() {
        lock (m_gate) {
            m_issuing?.Key.Dispose();
            m_issuing = null;
        }
        m_root.Dispose();
    }
}
/// <summary>The client side of an issuance exchange: dials the issuing authority, refuses to present a credential to
/// any transport key but the pinned one, proves possession of the run's key and returns the vouched identity.</summary>
public static class WorldIssuanceClient {
    /// <summary>The bound on one whole issuance exchange on the host clock: the dial, the credential's acquisition and
    /// the issuer's answer.</summary>
    public static TimeSpan ExchangeTimeout { get; } = TimeSpan.FromSeconds(seconds: 30);

    private static readonly IAttestationCodec IssuedCodec = new CborAttestationCodec();

    /// <summary>Asks the issuer at <paramref name="endpoint"/> to vouch for <paramref name="key"/>.</summary>
    /// <param name="endpoint">The issuing authority's QUIC endpoint.</param>
    /// <param name="remoteKeyHash">The lowercase SHA-256 fingerprint of the issuing authority's transport key; the
    /// credential is presented to no other.</param>
    /// <param name="credential">The client's sign-in credential source.</param>
    /// <param name="key">The run's key to have vouched for.</param>
    /// <param name="clock">The host clock the exchange's deadline runs on.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The vouched identity.</returns>
    /// <exception cref="InvalidOperationException">The issuer could not be reached in time, is not the pinned key, or
    /// refused; the message names which.</exception>
    public static WorldIssuedIdentity Issue(System.Net.EndPoint endpoint, string remoteKeyHash, IWorldSignInCredential credential, ECDsa key, TimeProvider clock, CancellationToken cancellationToken) {
        try {
            // Called from a synchronous signing seam (the federation proof) on a pool thread; the exchange runs on its
            // own tasks and this thread only waits, under the exchange's deadline.
            return Task.Run(function: () => IssueAsync(
                cancellationToken: cancellationToken,
                clock: clock,
                credential: credential,
                endpoint: endpoint,
                key: key,
                remoteKeyHash: remoteKeyHash
            ), cancellationToken: cancellationToken).GetAwaiter().GetResult();
        } catch (Exception exception) when (exception is IOException or OperationCanceledException or System.Net.Quic.QuicException or System.Net.Sockets.SocketException or PlatformNotSupportedException) {
            throw new InvalidOperationException(message: $"the issuer at {PeerEndpoint.Format(endpoint: endpoint)} could not be reached: {exception.Message}", innerException: exception);
        }
    }

    private static async Task<WorldIssuedIdentity> IssueAsync(System.Net.EndPoint endpoint, string remoteKeyHash, IWorldSignInCredential credential, ECDsa key, TimeProvider clock, CancellationToken cancellationToken) {
        using var deadline = new OperationDeadline(caller: cancellationToken, timeout: ExchangeTimeout, timeProvider: clock);
        var ct = deadline.Token;
        // A network of its own: an ephemeral transport identity for one exchange, disposed with it.
        using var network = new WorldPeerNetwork(timeProvider: clock);
        await using var stream = await network.ConnectAsync(endpoint: endpoint, ct: ct).ConfigureAwait(continueOnCapturedContext: false);
        var peer = (PeerStream)stream;
        var serverKeyHash = peer.Link.RemoteId.KeyHash;

        if (!string.Equals(a: serverKeyHash, b: remoteKeyHash, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidOperationException(message: $"the authority at {PeerEndpoint.Format(endpoint: endpoint)} presented transport key {serverKeyHash}, not the pinned {remoteKeyHash}; no credential was sent");
        }
        await HandshakeWireFormat.WriteHelloAsync(ct: ct, key: WorldFederationCodec.WireKey, stream: stream).ConfigureAwait(continueOnCapturedContext: false);
        var challenge = await WorldFederationCodec.ReadResponseAsync(ct: ct, stream: stream).ConfigureAwait(continueOnCapturedContext: false);

        if (!challenge.Ok || (challenge.Kind != ((byte)WorldFederationResponse.Challenge)) || (challenge.Body.Length == 0)) {
            throw new InvalidOperationException(message: $"the issuer did not open the exchange: {Describe(read: challenge)}");
        }
        var spki = key.ExportSubjectPublicKeyInfo();
        var bound = WorldFederationCodec.BindIssuanceChallenge(
            challenge: challenge.Body.Span,
            clientKeyHash: network.Peer.Id.KeyHash,
            serverKeyHash: serverKeyHash,
            subjectPublicKeyInfo: spki
        );

        await WorldFederationCodec.WriteRequestAsync(
            body: WorldFederationCodec.EncodeIssuance(
                credential: credential.Present(cancellationToken: ct, challenge: bound),
                possession: AttestationKeys.Sign(algorithm: AttestationAlgorithms.EcdsaP256Sha256, data: bound, key: key),
                subjectPublicKeyInfo: spki
            ),
            ct: ct,
            kind: WorldFederationRequest.Issue,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
        var answer = await WorldFederationCodec.ReadResponseAsync(ct: ct, stream: stream).ConfigureAwait(continueOnCapturedContext: false);

        if (!answer.Ok || (answer.Kind != ((byte)WorldFederationResponse.Issued))) {
            throw new InvalidOperationException(message: $"the issuer refused: {Describe(read: answer)}");
        }
        if (!WorldFederationCodec.TryDecodeIssued(body: answer.Body.Span, failure: out var failure, rootBinding: out var rootBinding, subjectBinding: out var subjectBinding)) {
            throw new InvalidOperationException(message: $"the issuer's answer does not decode: {failure}");
        }
        SignedAttestation decoded;

        try {
            decoded = AttestationProfile.Base.DecodeAttestation(codec: IssuedCodec, wire: subjectBinding);
        } catch (FormatException exception) {
            throw new InvalidOperationException(message: $"the issuer's subject binding does not decode: {exception.Message}", innerException: exception);
        }
        var target = IssuedCodec.DecodeKeyBindingPayload(bytes: decoded.PayloadBytes.Span).TargetId;

        if ((target.Subject is not { } subject) || (target.KeyHash != KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki))) {
            throw new InvalidOperationException(message: "the issuer vouched for a key other than this run's");
        }
        return new WorldIssuedIdentity(
            Chain: [rootBinding, subjectBinding],
            Domain: decoded.Header.Domain,
            NotAfter: decoded.Header.NotAfter,
            Subject: subject
        );
    }
    private static string Describe(WireFrameRead read) => (!read.Ok
        ? read.Failure.ToString()
        : ((read.Kind == ((byte)WorldFederationResponse.Refusal))
            ? Encoding.UTF8.GetString(bytes: read.Body.Span)
            : $"unexpected {((WorldFederationResponse)read.Kind)} frame"
        )
    );
}
