namespace Puck.World;

/// <summary>
/// The world's effective per-user storage: the store an installed provider built from the deployment's
/// <c>--storage-config-file</c>, and the explicit user-id (the world doc's <c>storage.userId</c>, overridden by
/// <c>--user-id</c>). <see cref="WorldStorageSyncHandle.Create"/> wires the owned-world sync engine when both the store
/// and the identity resolve; <c>storage.status</c> echoes them either way.
/// </summary>
/// <param name="Target">The per-user store, or <see langword="null"/> for none (local-only).</param>
/// <param name="UserId">The resolved explicit user-id override, or <see langword="null"/> to decline identity.</param>
internal sealed record WorldStorageSettings(Puck.Storage.ObjectStorageTarget? Target, string? UserId) {
    /// <summary>Resolves the effective settings: the CLI user-id wins over the authored one, and an absent CLI value keeps
    /// the authored default.</summary>
    /// <param name="defaults">The world-doc storage defaults.</param>
    /// <param name="target">The provider-built store, or <see langword="null"/> for none.</param>
    /// <param name="userIdOverride">The <c>--user-id</c> value, or <see langword="null"/>/empty for none.</param>
    /// <returns>The effective settings.</returns>
    public static WorldStorageSettings Resolve(WorldStorageDefaults defaults, Puck.Storage.ObjectStorageTarget? target, string? userIdOverride) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        return new WorldStorageSettings(
            Target: target,
            UserId: (string.IsNullOrWhiteSpace(value: userIdOverride)
            ? defaults.UserId
            : userIdOverride)
        );
    }
}
/// <summary>
/// Resolves the acting user to a per-user container id, or declines. Two implementations exist:
/// <see cref="ExplicitOverridePlayerStorageIdentityResolver"/> (the authored storage-section <c>userId</c> or its
/// <c>--user-id</c> reflection) and <see cref="DecliningPlayerStorageIdentityResolver"/> (the local-only default). A
/// resolution plus a store wires the owned-world sync engine; a decline leaves the catalog local-only.
/// </summary>
/// <remarks>
/// <para>Never a resolver that parses a storage access token: a storage token authenticates against the store and
/// carries no reliable claim about who is playing, only what the credential is scoped to. How the store authenticates
/// is its provider's concern; <c>storage.credential</c> asks the configured store whether this machine could reach it.</para>
/// <para>The identity (container) id is therefore authored for now, not discovered: the operator names their own user
/// id, a Guid, and the user id is the container. Discovering it belongs to the identity provider the deployment
/// configures, the same provider-selected way the store is chosen, and is not built yet.</para>
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
            : new ExplicitOverridePlayerStorageIdentityResolver(userId: settings.UserId)
        );
    }
}
/// <summary>The explicit-override resolver: the authored storage-section <c>userId</c> or its <c>--user-id</c> reflection —
/// the only identity source today (see <see cref="IPlayerStorageIdentityResolver"/>'s remarks). The value must pass
/// <see cref="WorldObjectId.IsValid"/> (a valid container name); a non-Guid override declines loudly rather than
/// inventing a container.</summary>
internal sealed class ExplicitOverridePlayerStorageIdentityResolver(string userId) : IPlayerStorageIdentityResolver {
    private readonly string m_userId = (userId ?? throw new ArgumentNullException(paramName: nameof(userId)));

    /// <inheritdoc/>
    public bool TryResolve(out Guid containerId, out string reason) {
        if (
            WorldObjectId.IsValid(value: m_userId) &&
            Guid.TryParse(
            input: m_userId,
            result: out containerId
        )
        ) {
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
