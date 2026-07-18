using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The storage console surface — <c>storage.status</c>, one honest Immediate echo of the player-catalog persistence
/// state (§2.5.6): the tier (local authoritative; cloud unwired this arc), the identity resolver's decision (an explicit
/// override id or why it declined), the reserved endpoint, and the per-catalog ordering/sync facts — the document
/// <see cref="WorldProfiles.Revision"/>, the last-synced cursor, the derived dirty flag, the storage version token, and
/// whether the last write hit an if-match precondition. It reports the TRUTH: with no cloud wired, identity is absent or
/// override-only, the endpoint is reserved, and the local copy is authoritative and unsynced. A SEPARATE module to keep
/// each class under its analyzer ceilings.
/// </summary>
/// <remarks>A pure read of local state (no protocol round-trip), so it is Immediate; the stdin barrier still lets a
/// preceding <c>profile.set</c>/<c>profile.save</c> settle before this reads the bumped revision and new token.</remarks>
internal sealed class WorldStorageCommandModule(WorldProfiles profiles, IPlayerStorageIdentityResolver identity, WorldStorageSettings settings) : ICommandModule {
    private readonly WorldProfiles m_profiles = profiles;
    private readonly IPlayerStorageIdentityResolver m_identity = identity;
    private readonly WorldStorageSettings m_settings = settings;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "storage.status",
            description: "Reports the honest player-catalog storage state (Immediate): tier (local authoritative; cloud unwired this arc), the identity resolver's decision, the reserved endpoint, and the per-catalog revision / last-synced cursor / derived dirty flag / storage version token / last-write precondition result.",
            handler: (_, args) => (args.Length > 0)
                ? new CommandResult(Output: "[storage.status: expected no arguments]") { IsError = true }
                : new CommandResult(Output: Describe())
        );
    }

    private string Describe() {
        var identity = (m_identity.TryResolve(containerId: out _, reason: out var reason) ? reason : $"declined — {reason}");
        var endpoint = (m_settings.Endpoint is { } value ? $"{value} (reserved)" : "none");
        var token = (m_profiles.VersionToken ?? "none");
        var lastWrite = (m_profiles.LastPreconditionFailed ? "precondition-failed" : "ok");

        return $"[storage.status: tier local (authoritative); cloud unwired | identity {identity} | endpoint {endpoint} | " +
            $"catalog revision {m_profiles.Revision} lastSynced {m_profiles.LastSyncedRevision} dirty {(m_profiles.Dirty ? "on" : "off")} " +
            $"token {token} lastWrite {lastWrite} | file {m_profiles.FilePath}]";
    }
}
