namespace Puck.World;

/// <summary>
/// The world's EFFECTIVE storage host-section values after the CLI reflection overrides the world-doc defaults:
/// the per-user cloud endpoint and the explicit user-id override. Both are RESERVED — nothing constructs an
/// Azure target from <see cref="Endpoint"/>, and <see cref="UserId"/> only feeds the identity resolver's
/// explicit-override source. <c>storage.status</c> echoes them.
/// </summary>
/// <param name="Endpoint">The resolved per-user blob endpoint, or <see langword="null"/> for none.</param>
/// <param name="UserId">The resolved explicit user-id override, or <see langword="null"/> to decline identity.</param>
internal sealed record WorldStorageSettings(string? Endpoint, string? UserId) {
    /// <summary>Resolves the effective settings by overlaying an optional CLI value over each world-doc default (the CLI
    /// reflection wins; an absent CLI value keeps the authored default).</summary>
    /// <param name="defaults">The world-doc storage defaults.</param>
    /// <param name="endpointOverride">The <c>--storage-uri</c> value, or <see langword="null"/>/empty for none.</param>
    /// <param name="userIdOverride">The <c>--user-id</c> value, or <see langword="null"/>/empty for none.</param>
    /// <returns>The effective settings.</returns>
    public static WorldStorageSettings Resolve(WorldStorageDefaults defaults, string? endpointOverride, string? userIdOverride) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        return new WorldStorageSettings(
            Endpoint: (string.IsNullOrWhiteSpace(value: endpointOverride) ? defaults.Endpoint : endpointOverride),
            UserId: (string.IsNullOrWhiteSpace(value: userIdOverride) ? defaults.UserId : userIdOverride)
        );
    }
}

/// <summary>
/// Resolves the acting user to a per-user container id, or DECLINES. Exactly two
/// implementations exist today — <see cref="ExplicitOverridePlayerStorageIdentityResolver"/> (a data-file / CLI <c>userId</c>) and
/// <see cref="DecliningPlayerStorageIdentityResolver"/> (the local-only default) — and NO token parsing or claims flow
/// (that requires a real ID token, not a parsed storage access token — unimplemented). The Entra <c>oid</c>
/// claim is a Guid and <c>Guid.ToString()</c> is a valid container name, so oid-as-container stays the target
/// mapping. The resolver's result feeds <c>storage.status</c>; nothing constructs an Azure target from it today.
/// </summary>
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

/// <summary>The explicit-override resolver: a data-file / CLI <c>userId</c> for dev boxes and agents. The value
/// must be an Entra <c>oid</c>-shaped Guid (a valid container name); a non-Guid override declines loudly rather than
/// inventing a container.</summary>
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
/// state <c>storage.status</c> reports when no override or credential is present.</summary>
internal sealed class DecliningPlayerStorageIdentityResolver : IPlayerStorageIdentityResolver {
    /// <inheritdoc/>
    public bool TryResolve(out Guid containerId, out string reason) {
        containerId = Guid.Empty;
        reason = "no user identity (per-user sync off, local-only)";

        return false;
    }
}
