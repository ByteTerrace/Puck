using Puck.Assets;
using Puck.Commands;
using Puck.Maths;
using Puck.Scripting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The World-side addon driver — the keystone that makes an addon a first-class PRINCIPAL: it composes an
/// <see cref="AddonHost"/> from the world document's <c>addons</c> rows (boot-time mount; Puck.Scripting is consumed,
/// never modified), and each tick drives every enabled addon over the SAME <see cref="IServerLink"/> as a human seat —
/// it writes the ABI snapshot for the body the addon holds Drive over, ticks the guest, translates its decoded
/// virtual-pad commands into a <see cref="PlayerIntent"/> over World's channel set, and submits it with principal
/// <c>addon:&lt;name&gt;</c>. The SERVER decides whether the
/// submission applies: while the addon holds Drive over the body it moves; on <c>world.revoke</c> the server drops its
/// intents (loud, once) and the body idles — a non-human principal exercising the neutral contract end-to-end.
/// </summary>
/// <remarks>
/// <para>The addon's body is the one it is granted Drive over — the grant IS the binding (there is no slot in the
/// document row). The driver DISCOVERS that body from the grant table and keeps driving it; it never authorizes itself
/// (capability checks live in the server), so after a revoke it keeps submitting and the server keeps refusing.
/// While genuinely granted, the driver sets the body's <see cref="IntentSource"/> to <see cref="IntentSource.Live"/>
/// once (the honest source for a driver that submits intents, like a remote client), so the wander producer yields to
/// the submitted stream and a revoke leaves the body still (idle) rather than resuming wander.</para>
/// <para>Zero steady-state allocation: the reused decode buffer lives in Puck.Scripting; this driver builds one
/// <see cref="PlayerIntent"/> struct per addon per tick and submits it. LOOPBACK-ONLY: the body-pose and grant reads
/// are in-process; a socket transport hands the addon its body over the wire and reads poses from snapshots.</para>
/// </remarks>
internal sealed class WorldAddonDriver : IDisposable {
    private readonly AddonHost? m_host;
    private readonly IServerLink m_link;
    private readonly WorldServer m_server;
    // The body each addon last held Drive over (name → 0-based entity index). Kept across a revoke so the driver keeps
    // submitting to it and the server enforces loudly; refreshed whenever a live grant names a (possibly new) body.
    private readonly Dictionary<string, int> m_boundBody = new(comparer: StringComparer.Ordinal);
    private bool m_disposed;

    private WorldAddonDriver(AddonHost? host, IServerLink link, WorldServer server) {
        m_host = host;
        m_link = link;
        m_server = server;
    }

    /// <summary>Builds a driver, mounting every ENABLED addon row of <paramref name="definition"/> through a fresh
    /// deterministic <see cref="AddonHost"/> (a bad row loads faulted, never crashing the run; one loud line per mount
    /// or fault). Returns a driver with a null host when the world declares no enabled addons — its per-tick pump is a
    /// no-op.</summary>
    /// <param name="definition">The boot world definition (its <c>addons</c> rows).</param>
    /// <param name="link">The client→server link the addon submits over.</param>
    /// <param name="server">The authoritative server (body poses + the grant table the body binding is read from).</param>
    /// <returns>The composed driver.</returns>
    public static WorldAddonDriver Create(WorldDefinition definition, IServerLink link, WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: link);
        ArgumentNullException.ThrowIfNull(argument: server);

        AddonHost? host = null;

        foreach (var row in definition.Addons) {
            if (!row.Enabled) {
                continue;
            }

            // Deferred host construction: only pay the Wasmtime engine when a world enables an addon. The host owns the
            // engine and the loader shares it (the loader compiles modules the host instantiates); the host disposes
            // the engine on Dispose.
            if (host is null) {
                var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);

                host = new AddonHost(engine: engine, loader: new WasmModuleLoader(engine: engine, assetSource: new FileSystemAssetSource()));
            }

            var descriptor = new AddonDescriptor(
                Name: row.Name,
                ModulePath: ResolvePath(modulePath: row.ModulePath),
                ModuleHash: (string.IsNullOrEmpty(value: row.Hash) ? null : row.Hash),
                Slot: null,
                FuelPerTick: ((row.Fuel == 0UL) ? null : (long)row.Fuel),
                Enabled: true
            );

            host.Add(descriptor: in descriptor);

            if (host.TryGet(instance: out var instance, name: row.Name)) {
                Console.Error.WriteLine(value: (instance.State == AddonState.Enabled)
                    ? $"[world.addon: mounted {row.Name} ({instance.Hash}) fuel {instance.FuelPerTick} — grant it a body: world.grant addon:{row.Name} drive body:<n> exclusive]"
                    : $"[world.addon: {row.Name} faulted — {instance.Fault.Detail}]");
            }
        }

        return new WorldAddonDriver(host: host, link: link, server: server);
    }

    /// <summary>Drives every enabled addon for the tick: for each, discover its granted Drive body (keeping the last if
    /// a revoke removed the grant), ensure the body reads its submitted stream (set it Live while genuinely granted),
    /// write the snapshot, tick the guest, and submit the translated intent with principal <c>addon:&lt;name&gt;</c>.
    /// The server enforces Drive at its drain; a faulted tick logs once and drives nothing.</summary>
    /// <param name="tick">The tick the submission is for (the same the seat submitter uses).</param>
    public void Tick(ulong tick) {
        if (m_host is null) {
            return;
        }

        foreach (var addon in m_host.Instances) {
            if (addon.State != AddonState.Enabled) {
                continue;
            }

            var principal = WorldPrincipal.Addon(name: addon.Name);

            // The grant IS the body binding. While genuinely granted, refresh the bound body and keep the body reading
            // its submitted stream (Live) so the wander producer yields and a later revoke leaves it idle.
            if (m_server.Grants.FirstDriveBody(principal: principal) is int granted) {
                m_boundBody[addon.Name] = granted;

                if ((m_server.Body(index: granted) is { } grantedBody) && (grantedBody.Source != IntentSource.Live)) {
                    m_link.SubmitCommand(command: new WorldCommand.SetControl(Principal: principal, EntityIndex: granted, Source: IntentSource.Live));
                }
            }

            if (!m_boundBody.TryGetValue(key: addon.Name, value: out var bodyIndex) || (m_server.Body(index: bodyIndex) is not { } body)) {
                continue;
            }

            var position = body.FixedPosition;
            var result = addon.Tick(snapshot: new AddonSnapshot(
                Tick: tick,
                PosLocalX: position.X.Value,
                PosLocalY: position.Y.Value,
                PosLocalZ: position.Z.Value,
                Buttons: 0u
            ));

            if (result.Status != AddonTickStatus.Ok) {
                // The tick that faults is the one that transitions to sticky-faulted; this loud line prints once.
                Console.Error.WriteLine(value: $"[world.addon: {addon.Fault.Detail}]");

                continue;
            }

            // Submit through the SAME link a seat uses — the server checks Drive over the body and applies or drops.
            m_link.SubmitIntent(submission: new IntentSubmission(
                Tick: tick,
                EntityIndex: bodyIndex,
                Intent: Translate(commands: addon.Commands),
                Principal: principal
            ));
        }
    }

    /// <summary>Disposes the addon host (one Wasmtime store per addon plus the engine — native resources).</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_host?.Dispose();
    }

    // Translate one tick's decoded virtual-pad commands into World's PlayerIntent over World's channel set. PadMove
    // writes the intent's OWN frame (X → strafe, Y → forward, no Y negation, matching WorldEngagement.Translate);
    // RightStick.X → the yaw Turn rate with the seat's look-right = negative-Turn sign; South/East → the
    // Primary/Secondary action lanes (held on a Started/Active phase). The pad ids World has no channel for
    // (North/West/shoulders/triggers) are ignored — a genre world binds a different SUBSET of the same neutral
    // vocabulary, never a different message.
    private static PlayerIntent Translate(ReadOnlySpan<AddonCommand> commands) {
        var forward = FixedQ4816.Zero;
        var strafe = FixedQ4816.Zero;
        var turn = FixedQ4816.Zero;
        var actions = ActionLanes.None;

        foreach (var command in commands) {
            switch (command.PadId) {
                case PadCommandId.Move:
                    strafe = FixedQ4816.FromRawBits(value: command.ValueX);
                    forward = FixedQ4816.FromRawBits(value: command.ValueY);

                    break;
                case PadCommandId.RightStick:
                    turn = -FixedQ4816.FromRawBits(value: command.ValueX);

                    break;
                case PadCommandId.South:
                    if (command.Phase is (CommandPhase.Started or CommandPhase.Active)) {
                        actions |= ActionLanes.Primary;
                    }

                    break;
                case PadCommandId.East:
                    if (command.Phase is (CommandPhase.Started or CommandPhase.Active)) {
                        actions |= ActionLanes.Secondary;
                    }

                    break;
                default:
                    break;
            }
        }

        return new PlayerIntent(MoveForward: forward, MoveStrafe: strafe, Turn: turn, Actions: actions);
    }

    // Resolve an addon module path: absolute as-is, else relative to the executable directory (Assets/** is Content-copied
    // beside the output, exactly how the world document itself is found at boot).
    private static string ResolvePath(string modulePath) {
        return (Path.IsPathRooted(path: modulePath) ? modulePath : Path.Combine(path1: AppContext.BaseDirectory, path2: modulePath));
    }
}
