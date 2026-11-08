using Puck.Commands;
using Puck.Storage;
using Puck.World.Server;
using System.Text;

namespace Puck.World;

/// <summary>
/// The storage console surface — <c>storage.status</c>, <c>storage.push</c>, <c>storage.pull</c>,
/// <c>storage.credential</c>. Status is one honest Immediate echo of the player-catalog persistence state: the wiring
/// disposition (local-only, or the wired container and the store's own description), the identity resolver's
/// decision, the catalog's revision / last-synced cursor / dirty flag / tracked-token count / last-write outcome
/// (ok / precondition-failed / failed) / cloud-discovery availability, and the last access-probe verdict. Push and
/// pull move whole owned-world documents against the per-user container with refuse-and-surface semantics — a stale
/// token, an unsynced cloud copy, a colliding cloud blob name, a document-gate refusal, and a transport error each land
/// as a named per-world line, never silently. A whole-catalog <c>storage.pull</c> also DISCOVERS cloud-only worlds by
/// listing the cloud namespace — a store that cannot list refuses that list BY NAME rather than reporting an empty
/// catalog (see <see cref="WorldOwnedWorldSync"/>'s class summary for the round-trip caveat). Credential asks the
/// configured store whether this machine could reach it and records the verdict for status to echo. A SEPARATE module
/// to keep each class under its analyzer ceilings.
/// </summary>
/// <remarks>All four are Immediate — none of them are simulation traffic — and all four run INLINE on the host's
/// frame loop, which is where the console pump drains. Status is a pure read and costs nothing; the other three are
/// control-plane IO, and while one is in flight the loop does not advance: a windowed session visibly stalls for as
/// long as the operation takes, bounded at 15s per blob for push/pull and 10s for the access probe. That is the
/// accepted shape for an operator verb nobody issues mid-play, not an oversight. The stdin barrier still lets a
/// preceding <c>identity.motion</c>/<c>identity.bindings.save</c> settle before any of them read the bumped revision.</remarks>
internal sealed class WorldStorageCommandModule(WorldOwnedWorlds profiles, IPlayerStorageIdentityResolver identity, WorldStorageSettings settings, WorldStorageSyncHandle sync,
    IObjectBlobStore store) : ICommandModule {
    private static readonly TimeSpan CredentialProbeTimeout = TimeSpan.FromSeconds(seconds: 10);

    private readonly WorldOwnedWorlds m_profiles = profiles;
    private readonly IPlayerStorageIdentityResolver m_identity = identity;
    private readonly WorldStorageSettings m_settings = settings;
    private readonly IObjectBlobStore m_store = store;
    private readonly WorldStorageSyncHandle m_sync = sync;
    private string m_credential = "not probed";
    private static readonly CommandForms StoragePushForms = CommandForms.Of(verb: "storage.push", "[<world-id>]");
    private static readonly CommandForms StoragePullForms = CommandForms.Of(verb: "storage.pull", "[<world-id>]");

    private string Describe() {
        var identity = (m_identity.TryResolve(
            containerId: out _,
            reason: out var reason
        )
            ? reason
            : $"declined — {reason}"
        );
        var catalog = ((m_sync.Engine is { } engine)
            ? $"catalog revision {m_profiles.Revision} lastSynced {engine.LastSyncedRevision} dirty {(engine.Dirty
                ? "on"
                : "off")} tracked {engine.TrackedCount} lastWrite {Word(outcome: engine.LastWrite)} lastClaim {engine.LastClaimDetail} discovery on"
            : $"catalog revision {m_profiles.Revision} (local authoritative, discovery off)"
        );

        return $"[storage.status: {m_sync.Disposition} | identity {identity} | store {(m_settings.Target?.Description ?? "none")} | credential {m_credential} | {catalog} | file {m_profiles.FilePath}]";
    }
    private CommandResult Probe() {
        if (m_settings.Target is not { } target) {
            return CommandResult.Error(output: "[storage.credential: no store configured — nothing authenticates]");
        }

        var status = m_store.ProbeAccessAsync(
            target: target,
            timeout: CredentialProbeTimeout
        ).AsTask().GetAwaiter().GetResult();

        m_credential = (status.Available
            ? $"available{((status.ExpiresOn is { } expires) ? $" (expires {expires:u})" : "")}"
            : $"unavailable — {status.Detail}"
        );

        return (status.Available
            ? new CommandResult(Output: $"[storage.credential: {status.Detail}{((status.ExpiresOn is { } expiresOn) ? $", expires {expiresOn:u}" : "")} | {target.Description}]")
            : CommandResult.Error(output: $"[storage.credential: no access — {status.Detail} | {target.Description}]")
        );
    }
    private CommandResult Run(string? id, string verb, Func<WorldOwnedWorldSync, string?, IReadOnlyList<WorldSyncOutcome>> operation) {
        if (m_sync.Engine is not { } engine) {
            return CommandResult.Error(output: $"[{verb}: {m_sync.Disposition}]");
        }

        var outcomes = operation(
            engine,
            id
        );
        var builder = new StringBuilder();

        builder.Append(value: '[').Append(value: verb).Append(value: ':');
        for (var index = 0; (index < outcomes.Count); index++) {
            var outcome = outcomes[index];

            builder.Append(value: ((index == 0)
                ? " "
                : " | "));
            builder.Append(value: outcome.Id).Append(value: ' ').Append(value: (outcome.Ok
                ? outcome.Detail
                : $"refused — {outcome.Detail}"));
        }
        builder.Append(value: ']');
        return new CommandResult(Output: builder.ToString());
    }
    // The last push's ACTUAL outcome, distinguishing a transport failure from a precondition failure — a status
    // line must never assert the opposite of what the verb beside it just printed.
    private static string Word(WorldAuthorityStoreOutcomeKind outcome) => outcome switch {
        WorldAuthorityStoreOutcomeKind.PreconditionFailed => "precondition-failed",
        WorldAuthorityStoreOutcomeKind.AlreadyExists => "already-exists",
        WorldAuthorityStoreOutcomeKind.Failed => "failed",
        _ => "ok",
    };

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "storage.status",
            description: "Reports the honest player-catalog storage state (Immediate): the wiring disposition, the identity resolver's decision, the configured store's description, the per-catalog revision / last-synced cursor / dirty flag / tracked-token count / last-write outcome (ok / precondition-failed / failed), and the last storage.credential verdict.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(
                args: args,
                verb: "storage.status"
            ) is { } refusal)
            ? refusal
            : new CommandResult(Output: Describe()))
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "storage.push",
            description: $"Pushes one owned world, or with no world the whole catalog, to the per-user cloud container: {StoragePushForms.Spelling}. First contact is create-only; a tracked world pushes with its token, and a moved cloud copy refuses with the remedy. A world whose id cannot address a blob of its own refuses by name instead of pushing (Immediate, up to 15s per blob — a basis chain multiplies this).",
            forms: StoragePushForms,
            handler: (_, args) => ((args.Count > 1)
            ? StoragePushForms.Refuse(got: args[1].ToString())
            : Run(
                    id: ((args.Count == 1)
                ? args[0].ToString()
                : null),
                    verb: "storage.push",
                    operation: static (engine, id) => engine.Push(id: id)
                ))
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "storage.pull",
            forms: StoragePullForms,
            description: $"Pulls one owned world, or with no world every local/tracked/cloud-discovered one, from the per-user cloud container: {StoragePullForms.Spelling}. Each is validated through the boot loader's gate before adopting it. A whole-catalog pull also lists the cloud puck/worlds/ namespace and adopts any id it does not already know; a cloud object whose name no owned-world id can address refuses by name instead of being silently skipped. A pulled copy replaces the owned identity in place, so every seat bound to it follows; while a tape records, every adoption refuses by name (PullWhileRecording), because a tape never carries an owned document (Immediate, up to 15s per blob — a basis chain multiplies this).",
            handler: (_, args) => ((args.Count > 1)
            ? StoragePullForms.Refuse(got: args[1].ToString())
            : Run(
                    id: ((args.Count == 1)
                ? args[0].ToString()
                : null),
                    verb: "storage.pull",
                    operation: static (engine, id) => engine.Pull(id: id)
                ))
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "storage.credential",
            description: "Probes whether this machine could authenticate to the configured store right now, through its provider (a store that needs no credential answers available). A presence check, never a prompt: it records its verdict for storage.status to echo (Immediate, up to 10s).",
            handler: (_, args) => ((CommandResult.RequireNoArguments(
                args: args,
                verb: "storage.credential"
            ) is { } refusal)
            ? refusal
            : Probe())
        );
    }
}
