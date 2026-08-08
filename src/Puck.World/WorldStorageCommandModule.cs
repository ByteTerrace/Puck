using Puck.Commands;
using Puck.Storage;
using Puck.World.Server;
using System.Text;

namespace Puck.World;

/// <summary>
/// The storage console surface — <c>storage.status</c>, <c>storage.push</c>, <c>storage.pull</c>,
/// <c>storage.credential</c>. Status is one honest Immediate echo of the player-catalog persistence state: the wiring
/// disposition (local-only, or the wired container, route, AND discovery-endpoint decision), the identity resolver's
/// decision, the endpoint, the discovery endpoint, the catalog's revision / last-synced cursor / dirty flag /
/// tracked-token count / last-write outcome (ok / precondition-failed / failed) / cloud-discovery availability, and
/// the last credential-probe verdict. Push and pull move whole owned-world documents against the per-user container
/// with refuse-and-surface semantics — a stale token, an unsynced cloud copy, a colliding cloud blob name, a
/// document-gate refusal, and a transport error each land as a named per-world line, never silently. A
/// whole-catalog <c>storage.pull</c> also DISCOVERS cloud-only worlds by listing the cloud namespace — an edge-shaped
/// endpoint with no discovery endpoint authored refuses that list BY NAME (the platform edge cannot serve one at
/// all) rather than reporting an empty catalog (see <see cref="WorldOwnedWorldSync"/>'s class summary for the
/// round-trip caveat). Credential probes the ambient Azure credential and records the verdict for status to echo. A
/// SEPARATE module to keep each class under its analyzer ceilings.
/// </summary>
/// <remarks>All four are Immediate — none of them are simulation traffic — and all four run INLINE on the host's
/// frame loop, which is where the console pump drains. Status is a pure read and costs nothing; the other three are
/// control-plane IO, and while one is in flight the loop does not advance: a windowed session visibly stalls for as
/// long as the operation takes, bounded at 15s per blob for push/pull and 10s for the credential probe. That is the
/// accepted shape for an operator verb nobody issues mid-play, not an oversight. The stdin barrier still lets a
/// preceding <c>identity.motion</c>/<c>identity.bindings.save</c> settle before any of them read the bumped revision.</remarks>
internal sealed class WorldStorageCommandModule(WorldOwnedWorlds profiles, IPlayerStorageIdentityResolver identity, WorldStorageSettings settings, WorldStorageSyncHandle sync) : ICommandModule {
    private static readonly TimeSpan CredentialProbeTimeout = TimeSpan.FromSeconds(seconds: 10);
    private readonly WorldOwnedWorlds m_profiles = profiles;
    private readonly IPlayerStorageIdentityResolver m_identity = identity;
    private readonly WorldStorageSettings m_settings = settings;
    private readonly WorldStorageSyncHandle m_sync = sync;
    private string m_credential = "not probed";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "storage.status",
            description: "Reports the honest player-catalog storage state (Immediate): the wiring disposition (including the discovery-endpoint decision), the identity resolver's decision, the endpoint, the discovery endpoint, the per-catalog revision / last-synced cursor / dirty flag / tracked-token count / last-write outcome (ok / precondition-failed / failed), and the last storage.credential verdict.",
            handler: (_, args) => ((args.Count > 0)
                ? CommandResult.Error(output: "[storage.status: expected no arguments]")
                : new CommandResult(Output: Describe()))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "storage.push",
            description: "Pushes one owned world (storage.push <world-id>) or the whole catalog (no argument) to the per-user cloud container. First contact is create-only; a tracked world pushes with its token, and a moved cloud copy refuses with the remedy. A world whose id cannot address a blob of its own refuses by name instead of pushing (Immediate, up to 15s per world).",
            handler: (_, args) => ((args.Count > 1)
                ? CommandResult.Error(output: "[storage.push: expected at most one argument (a world id)]")
                : Run(id: ((args.Count == 1) ? args[0].ToString() : null), verb: "storage.push", operation: static (engine, id) => engine.Push(id: id)))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "storage.pull",
            description: "Pulls one owned world (storage.pull <world-id>) or every local/tracked/cloud-discovered one (no argument) from the per-user cloud container, validating each through the boot loader's gate before adopting it. A whole-catalog pull also lists the cloud puck/worlds/ namespace and adopts any id it does not already know; a cloud object whose name no owned-world id can address refuses by name instead of being silently skipped (Immediate, up to 15s per world).",
            handler: (_, args) => ((args.Count > 1)
                ? CommandResult.Error(output: "[storage.pull: expected at most one argument (a world id)]")
                : Run(id: ((args.Count == 1) ? args[0].ToString() : null), verb: "storage.pull", operation: static (engine, id) => engine.Pull(id: id)))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "storage.credential",
            description: "Probes whether the ambient Azure credential can issue a storage token from this machine — the credential the cloud tier authenticates a service-URI endpoint with (a player signs in ambiently, a hosted server runs as a managed identity; no app registration is involved). A presence check, never a prompt: it records its verdict for storage.status to echo (Immediate, up to 10s).",
            handler: (_, args) => ((args.Count > 0)
                ? CommandResult.Error(output: "[storage.credential: expected no arguments]")
                : Probe())
        );
    }

    private CommandResult Run(string? id, string verb, Func<WorldOwnedWorldSync, string?, IReadOnlyList<WorldSyncOutcome>> operation) {
        if (m_sync.Engine is not { } engine) {
            return CommandResult.Error(output: $"[{verb}: {m_sync.Disposition}]");
        }

        var outcomes = operation(engine, id);
        var builder = new StringBuilder();

        builder.Append(value: '[').Append(value: verb).Append(value: ':');
        for (var index = 0; (index < outcomes.Count); index++) {
            var outcome = outcomes[index];

            builder.Append(value: ((index == 0) ? " " : " | "));
            builder.Append(value: outcome.Id).Append(value: ' ').Append(value: (outcome.Ok ? outcome.Detail : $"refused — {outcome.Detail}"));
        }
        builder.Append(value: ']');
        return new CommandResult(Output: builder.ToString());
    }

    // The probe answers "would the cloud let this machine in", which is only ON this session's path when the endpoint
    // is a service URI — a connection string authenticates with the account key it carries instead. Both readings are
    // reported so the verdict is never mistaken for a verdict about the wrong thing.
    private CommandResult Probe() {
        var status = AzureBlobCredentialProbe.ProbeAsync(timeout: CredentialProbeTimeout).AsTask().GetAwaiter().GetResult();
        var route = ((m_settings.Endpoint is not { Length: > 0 } endpoint)
            ? "no endpoint — nothing authenticates"
            : (Uri.TryCreate(uriString: endpoint, uriKind: UriKind.Absolute, result: out _)
                ? "this session's endpoint is a service URI — it authenticates with exactly this credential"
                : "this session's endpoint is a connection string — it authenticates with the account key instead"));

        m_credential = (status.Available
            ? $"available (expires {status.ExpiresOn:u})"
            : $"unavailable — {status.Detail}");

        return (status.Available
            ? new CommandResult(Output: $"[storage.credential: {status.Detail}, expires {status.ExpiresOn:u} | {route}]")
            : CommandResult.Error(output: $"[storage.credential: no storage token — {status.Detail} | {route}]"));
    }
    private string Describe() {
        var identity = (m_identity.TryResolve(containerId: out _, reason: out var reason) ? reason : $"declined — {reason}");
        var endpoint = RedactedEndpoint(value: m_settings.Endpoint);
        var discoveryEndpoint = RedactedEndpoint(value: m_settings.DiscoveryEndpoint);
        var catalog = ((m_sync.Engine is { } engine)
            ? $"catalog revision {m_profiles.Revision} lastSynced {engine.LastSyncedRevision} dirty {(engine.Dirty ? "on" : "off")} tracked {engine.TrackedCount} lastWrite {Word(outcome: engine.LastWrite)} discovery on"
            : $"catalog revision {m_profiles.Revision} (local authoritative, discovery off)");

        return $"[storage.status: {m_sync.Disposition} | identity {identity} | endpoint {endpoint} | discoveryEndpoint {discoveryEndpoint} | credential {m_credential} | {catalog} | file {m_profiles.FilePath}]";
    }

    // A connection string carries the account key; only a URI endpoint is safe to echo. Shared by endpoint and
    // discoveryEndpoint — both are the same authored-value shape (a URI or a dev/emulator connection string).
    private static string RedactedEndpoint(string? value) {
        return ((value is not { Length: > 0 } present) ? "none"
            : (Uri.TryCreate(uriString: present, uriKind: UriKind.Absolute, result: out _) ? present : "a connection string (redacted — it carries the account key)"));
    }

    // The last push's ACTUAL outcome. This used to read the precondition bit alone, so a session in which every push
    // was refused by a transport error still reported "ok" — a status line asserting the opposite of what the verb
    // beside it had just printed.
    private static string Word(WorldSyncWriteOutcome outcome) => outcome switch {
        WorldSyncWriteOutcome.PreconditionFailed => "precondition-failed",
        WorldSyncWriteOutcome.Failed => "failed",
        _ => "ok",
    };
}
