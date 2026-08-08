namespace Puck.World;

/// <summary>
/// The world's EFFECTIVE storage host-section values after the CLI reflection overrides the world-doc defaults:
/// the per-user cloud endpoint, the explicit user-id override, and the direct-to-account discovery endpoint.
/// <see cref="WorldStorageSyncHandle.Create"/> wires the owned-world sync engine from them when both the endpoint
/// and identity resolve; <c>storage.status</c> echoes them either way.
/// </summary>
/// <param name="Endpoint">The resolved per-user blob endpoint, or <see langword="null"/> for none.</param>
/// <param name="UserId">The resolved explicit user-id override, or <see langword="null"/> to decline identity.</param>
/// <param name="DiscoveryEndpoint">The resolved direct-to-account discovery endpoint container LIST uses when
/// <see cref="Endpoint"/> is edge-shaped, or <see langword="null"/> for none (an edge-shaped target with no
/// discovery endpoint refuses LIST by name — see <c>AzureBlobObjectStorageTarget.DirectEndpoint</c>).</param>
internal sealed record WorldStorageSettings(string? Endpoint, string? UserId, string? DiscoveryEndpoint) {
    /// <summary>Resolves the effective settings by overlaying an optional CLI value over each world-doc default (the CLI
    /// reflection wins; an absent CLI value keeps the authored default).</summary>
    /// <param name="defaults">The world-doc storage defaults.</param>
    /// <param name="endpointOverride">The <c>--storage-uri</c> value, or <see langword="null"/>/empty for none.</param>
    /// <param name="userIdOverride">The <c>--user-id</c> value, or <see langword="null"/>/empty for none.</param>
    /// <param name="discoveryEndpointOverride">The <c>--storage-discovery-uri</c> value, or <see langword="null"/>/empty
    /// for none.</param>
    /// <returns>The effective settings.</returns>
    public static WorldStorageSettings Resolve(WorldStorageDefaults defaults, string? endpointOverride, string? userIdOverride, string? discoveryEndpointOverride) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        return new WorldStorageSettings(
            Endpoint: (string.IsNullOrWhiteSpace(value: endpointOverride) ? defaults.Endpoint : endpointOverride),
            UserId: (string.IsNullOrWhiteSpace(value: userIdOverride) ? defaults.UserId : userIdOverride),
            DiscoveryEndpoint: (string.IsNullOrWhiteSpace(value: discoveryEndpointOverride) ? defaults.DiscoveryEndpoint : discoveryEndpointOverride)
        );
    }
}

/// <summary>
/// Resolves the acting user to a per-user container id, or DECLINES. Two implementations exist:
/// <see cref="ExplicitOverridePlayerStorageIdentityResolver"/> (the authored storage-section <c>userId</c> or its
/// <c>--user-id</c> reflection) and <see cref="DecliningPlayerStorageIdentityResolver"/> (the local-only default). A
/// resolution plus an endpoint wires the owned-world sync engine; a decline leaves the catalog local-only.
/// </summary>
/// <remarks>
/// <para>NEVER a resolver that parses a storage access token: that stance is an owner ruling, not an oversight — a
/// storage token authenticates against blob storage and carries no reliable claim about WHO is playing, only what the
/// credential is scoped to.</para>
/// <para>Nor an app registration. Game clients ARE users: they never receive one, they authenticate ambiently
/// (developer tooling, the OS broker, a shared token cache), and a hosted server runs as a user-assigned managed
/// identity — one <c>DefaultAzureCredential</c> covers both, which is exactly what the data plane already does
/// (<c>Puck.Storage.AzureBlobObjectBlobStoreBackend</c>). <c>storage.credential</c> reports whether that ambient
/// credential can actually issue a storage token from this machine.</para>
/// <para>The identity (container) id is therefore AUTHORED for now, not discovered: an Entra <c>oid</c> is a Guid and
/// <c>Guid.ToString()</c> is a valid container name, so the operator names their own oid and oid-as-container stays
/// the mapping. The recorded destination for discovering it without a registration is the SAME ambient credential
/// taking a Microsoft Graph token and reading <c>/v1.0/me</c>, whose <c>id</c> IS the oid — on a user's machine only;
/// a managed identity has no <c>/me</c>. That is deliberately not built yet, and it is written down here so nobody
/// reinvents an app registration to reach it.</para>
/// </remarks>
internal interface IPlayerStorageIdentityResolver {
    /// <summary>Attempts to resolve the acting user to a per-user container id.</summary>
    /// <param name="containerId">The resolved container id on success; <see cref="Guid.Empty"/> otherwise.</param>
    /// <param name="reason">A human-readable description of the resolution (the resolved id, or why it declined) for
    /// <c>storage.status</c>.</param>
    /// <returns><see langword="true"/> when an identity resolved; <see langword="false"/> when it declined.</returns>
    bool TryResolve(out Guid containerId, out string reason);

    /// <summary>Builds the resolver from the effective storage settings: an explicit user-id yields the override
    /// resolver, its absence yields the declining resolver.</summary>
    /// <param name="settings">The effective storage settings.</param>
    /// <returns>The selected resolver.</returns>
    static IPlayerStorageIdentityResolver Create(WorldStorageSettings settings) {
        ArgumentNullException.ThrowIfNull(argument: settings);

        return (string.IsNullOrWhiteSpace(value: settings.UserId)
            ? new DecliningPlayerStorageIdentityResolver()
            : new ExplicitOverridePlayerStorageIdentityResolver(userId: settings.UserId));
    }
}

/// <summary>The explicit-override resolver: the authored storage-section <c>userId</c> or its <c>--user-id</c> reflection —
/// the only identity source today (see <see cref="IPlayerStorageIdentityResolver"/>'s remarks). The value must be an
/// Entra <c>oid</c>-shaped Guid (a valid container name); a non-Guid override declines loudly rather than inventing a
/// container.</summary>
internal sealed class ExplicitOverridePlayerStorageIdentityResolver(string userId) : IPlayerStorageIdentityResolver {
    private readonly string m_userId = (userId ?? throw new ArgumentNullException(paramName: nameof(userId)));

    /// <inheritdoc/>
    public bool TryResolve(out Guid containerId, out string reason) {
        if (Guid.TryParse(input: m_userId, result: out containerId) && (containerId != Guid.Empty)) {
            reason = $"explicit override userId={containerId}";

            return true;
        }

        containerId = Guid.Empty;
        reason = $"explicit override userId '{m_userId}' is not a container Guid; declining (local-only)";

        return false;
    }
}

/// <summary>The declining resolver — the local-only default: no user identity, per-user sync off. The honest
/// state <c>storage.status</c> reports when no user-id is authored.</summary>
internal sealed class DecliningPlayerStorageIdentityResolver : IPlayerStorageIdentityResolver {
    /// <inheritdoc/>
    public bool TryResolve(out Guid containerId, out string reason) {
        containerId = Guid.Empty;
        reason = "no user identity (per-user sync off, local-only)";

        return false;
    }
}
