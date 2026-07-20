using System.Numerics;
using System.Text;

using Puck.Assets;
using Puck.Commands;
using Puck.Maths;
using Puck.Scripting;

namespace Puck.Demo.Overworld;

/// <summary>
/// The demo's addon runtime seam — the ONE type the render node names for the whole WASM-addon subsystem, so the
/// scripting-type coupling stays off the node and frame source (both at their analyzer coupling ceilings). It carries
/// two concerns behind one interface (a deliberate consolidation the CA1506 ceiling forces): the node's per-tick PUMP
/// (<see cref="EnsureRoster"/> seats each ghost's padless roster occupant, <see cref="OwnsSlot"/> tells the human
/// sampler which slots to skip, <see cref="Apply"/> ticks each ghost and folds its virtual-pad commands into the two
/// intent rows), and the <c>addon</c> console VERBS (<see cref="ListAddons"/> / <see cref="SetAddonEnabled"/>, the
/// <c>addon list</c> / <c>addon enable</c> / <c>addon disable</c> control plane the <c>AddonCommandModule</c>
/// drives). Reached by the command module through <c>ICreatorModeHost.AddonControl</c> off the render-node root, exactly
/// like the overworld control verbs reach their host. Host-side lifecycle only; the deterministic simulation never
/// learns it exists.
/// </summary>
internal interface IAddonControlHost : IDisposable {
    /// <summary>Seats each addon ghost's padless roster occupant at its exclusive slot if not already seated, so the
    /// addon has a live body to read and drive. Runs each console-mode frame before the human samplers.</summary>
    /// <param name="world">The live world to seat occupants in.</param>
    void EnsureRoster(OverworldWorld world);

    /// <summary>Whether <paramref name="slot"/> is owned exclusively by an addon ghost (the human pad sampler and the
    /// console roster reconcile skip these slots).</summary>
    /// <param name="slot">The roster slot to test.</param>
    bool OwnsSlot(int slot);

    /// <summary>Ticks every enabled, slot-owning addon against its ghost's live local position and OVERWRITES that
    /// slot's two intent rows with the returned virtual-pad commands — the console-mode pump, called after the human
    /// intent fill and before the world advances.</summary>
    /// <param name="world">The live world (the ghost's local position is read, never written).</param>
    /// <param name="firstTickIntents">The frame's first-sub-tick intent row (carries the edge commands).</param>
    /// <param name="heldIntents">The frame's held intent row (movement + held state only).</param>
    void Apply(OverworldWorld world, PlayerIntent[] firstTickIntents, PlayerIntent[] heldIntents);

    /// <summary>One line per loaded addon: petname, content hash, slot, last-tick fuel, and state
    /// (<c>ENABLED</c>/<c>DISABLED</c>/<c>FAULTED(&lt;kind&gt;)</c>) — the <c>addon list</c> verb.</summary>
    string ListAddons();

    /// <summary>Enables (re-instantiates a fresh store, clearing any sticky fault) or disables the named addon —
    /// the <c>addon enable</c>/<c>addon disable</c> verbs. Returns a console status line naming the addon's petname,
    /// or an unknown-addon line.</summary>
    /// <param name="name">The addon name.</param>
    /// <param name="enabled">Whether to enable or disable it.</param>
    string SetAddonEnabled(string name, bool enabled);

    /// <summary>Reloads the named addon's module from disk and swaps in a fresh store — the <c>addon reload</c>
    /// verb, the in-session edit loop: same roster body, new brain. Returns a console status line; when the module's
    /// content actually changed, the line says so by petname ("X became Y").</summary>
    /// <param name="name">The addon name.</param>
    string ReloadAddon(string name);
}

/// <summary>
/// The demo's WASM-addon runtime: it composes the shared-substrate <see cref="AddonHost"/> from a run document's
/// descriptors (resolving each ghost's exclusive roster slot), seats and drives each ghost each console-mode tick,
/// translates a tick's decoded <see cref="AddonCommand"/>s into the overworld's <see cref="PlayerIntent"/>, and
/// renders the <c>addon</c> verbs' status lines. All scripting-type coupling lives here so the render node and frame
/// source (both at their coupling ceilings) name only <see cref="IAddonControlHost"/>.
/// </summary>
internal sealed class AddonRuntime : IAddonControlHost {
    private readonly AddonHost m_host;
    private readonly HashSet<int> m_slots = [];

    private AddonRuntime(AddonHost host) {
        m_host = host;
    }

    /// <summary>Builds a runtime from a run document's descriptors, resolving each addon's exclusive roster slot
    /// (a declared <c>1..MaxPlayers-1</c> slot, else the first free non-human slot), registering it, and logging one
    /// load line (petname + hash) per addon. A bad addon loads faulted rather than throwing.</summary>
    /// <param name="descriptors">The neutral addon load requests (from <c>document.Addons</c>); module paths are
    /// already resolved to absolute by the graph builder, so a plain file-system asset source reads them.</param>
    /// <returns>The composed runtime, or <see langword="null"/> when <paramref name="descriptors"/> is empty.</returns>
    public static AddonRuntime? Create(IReadOnlyList<AddonDescriptor> descriptors) {
        ArgumentNullException.ThrowIfNull(argument: descriptors);

        if (descriptors.Count == 0) {
            return null;
        }

        var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        var host = new AddonHost(engine: engine, loader: new WasmModuleLoader(assetSource: new FileSystemAssetSource(), engine: engine));
        var runtime = new AddonRuntime(host: host);
        var claimed = new HashSet<int>();

        foreach (var descriptor in descriptors) {
            if ((descriptor.Slot is int declared) && (declared >= 1) && (declared < OverworldWorld.MaxPlayers)) {
                _ = claimed.Add(item: declared);
            }
        }

        foreach (var descriptor in descriptors) {
            var slot = ResolveSlot(claimed: claimed, declared: descriptor.Slot);
            var resolved = (descriptor with { Slot = ((slot >= 0) ? slot : null) });

            host.Add(descriptor: in resolved);

            if (slot >= 0) {
                _ = claimed.Add(item: slot);
                _ = runtime.m_slots.Add(item: slot);
            }

            Log(host: host, name: descriptor.Name, slot: slot);
        }

        return runtime;
    }

    /// <inheritdoc/>
    public void EnsureRoster(OverworldWorld world) {
        ArgumentNullException.ThrowIfNull(argument: world);

        foreach (var slot in m_slots) {
            _ = world.AddPlayerAtSlot(playerId: AddonPlayerId(slot: slot), slot: slot);
        }
    }

    /// <inheritdoc/>
    public bool OwnsSlot(int slot) {
        return m_slots.Contains(item: slot);
    }

    /// <inheritdoc/>
    public void Apply(OverworldWorld world, PlayerIntent[] firstTickIntents, PlayerIntent[] heldIntents) {
        ArgumentNullException.ThrowIfNull(argument: world);
        ArgumentNullException.ThrowIfNull(argument: firstTickIntents);
        ArgumentNullException.ThrowIfNull(argument: heldIntents);

        foreach (var addon in m_host.Instances) {
            if (addon.Slot is not int slot) {
                continue;
            }

            if ((slot < 0) || (slot >= firstTickIntents.Length) || (slot >= heldIntents.Length)) {
                continue;
            }

            if (addon.State != AddonState.Enabled) {
                firstTickIntents[slot] = PlayerIntent.None;
                heldIntents[slot] = PlayerIntent.None;

                continue;
            }

            var local = world.LocalPositionForSlot(slot: slot);
            var snapshot = new AddonSnapshot(Buttons: 0u, PosLocalX: local.X.Value, PosLocalY: local.Y.Value, PosLocalZ: local.Z.Value, Tick: world.CurrentTick);
            var result = addon.Tick(snapshot: in snapshot);

            if (result.Status != AddonTickStatus.Ok) {
                // The tick that faults is the one that transitions to sticky-faulted (every later tick short-circuits at
                // the state check above), so this loud line prints exactly once per fault.
                Console.Error.WriteLine(value: $"[addon] {addon.Fault.Detail}");
                firstTickIntents[slot] = PlayerIntent.None;
                heldIntents[slot] = PlayerIntent.None;

                continue;
            }

            var (first, held) = TranslateToIntent(commands: addon.Commands);

            firstTickIntents[slot] = first;
            heldIntents[slot] = held;
        }
    }

    /// <inheritdoc/>
    public string ListAddons() {
        var instances = m_host.Instances;

        if (instances.Count == 0) {
            return "[addon: no addons loaded]";
        }

        var builder = new StringBuilder();

        for (var index = 0; (index < instances.Count); ++index) {
            if (index > 0) {
                _ = builder.Append(value: '\n');
            }

            _ = builder.Append(value: DescribeInstance(addon: instances[index]));
        }

        return builder.ToString();
    }

    /// <inheritdoc/>
    public string SetAddonEnabled(string name, bool enabled) {
        var verb = (enabled ? "enable" : "disable");

        if (!m_host.TryGet(instance: out var instance, name: name)) {
            return $"[addon {verb} {name}: unknown addon '{name}']";
        }

        _ = m_host.SetEnabled(enabled: enabled, name: name);

        var petname = ContentPetname.From(hashHex: $"{instance.Hash.Value:x16}");

        if (!enabled) {
            return $"[addon disable {name}: ok — {petname} idled]";
        }

        return ((instance.State == AddonState.Enabled)
            ? $"[addon enable {name}: ok — {petname} resumed]"
            : $"[addon enable {name}: {petname} could not resume — {StateLabel(addon: instance)}]");
    }

    /// <inheritdoc/>
    public string ReloadAddon(string name) {
        return $"[addon reload {name}: {m_host.Reload(name: name)}]";
    }

    /// <summary>Disposes the addon host (one Wasmtime store per addon, plus the engine — native resources).</summary>
    public void Dispose() {
        m_host.Dispose();
    }

    // Translates one tick's decoded pad commands into the overworld's two intent rows. PadMove writes PlayerIntent.Move
    // in the intent's OWN frame — X = strafe, Y = forward — with NO Y negation (the addon speaks the intent frame; the
    // raw-pad path negates only because it reads raw stick space). Edge commands (interact/cycle/jump edges) land on the
    // first-sub-tick row alone so a Started edge fires once. Unbound pads are ignored, never faulted.
    private static (PlayerIntent First, PlayerIntent Held) TranslateToIntent(ReadOnlySpan<AddonCommand> commands) {
        var move = Vector2.Zero;
        var jumpHeld = false;
        var jumpPressed = false;
        var jumpReleased = false;
        var interactPressed = false;
        var cyclePressed = false;
        var runHeld = false;

        foreach (var command in commands) {
            switch (command.PadId) {
                case PadCommandId.Move:
                    move = new Vector2(x: ToFloat(raw: command.ValueX), y: ToFloat(raw: command.ValueY));

                    break;
                case PadCommandId.South:
                    jumpHeld |= ((command.Phase == CommandPhase.Started) || (command.Phase == CommandPhase.Active));
                    jumpPressed |= (command.Phase == CommandPhase.Started);
                    jumpReleased |= (command.Phase == CommandPhase.Completed);

                    break;
                case PadCommandId.North:
                    interactPressed |= (command.Phase == CommandPhase.Started);

                    break;
                case PadCommandId.ShoulderRight:
                    cyclePressed |= (command.Phase == CommandPhase.Started);

                    break;
                case PadCommandId.ShoulderLeft:
                    runHeld |= (command.Phase == CommandPhase.Active);

                    break;
                default:
                    break;
            }
        }

        var held = new PlayerIntent(JumpHeld: jumpHeld, JumpPressed: false, JumpReleased: false, Move: move, RunHeld: runHeld);
        var first = (held with { CyclePressed = cyclePressed, InteractPressed = interactPressed, JumpPressed = jumpPressed, JumpReleased = jumpReleased });

        return (First: first, Held: held);
    }

    // FixedQ4816 in [-1,1] is exact in float32, so this fixed->float is lossless; the sim re-quantizes via FromDouble
    // identically to the human pad path, so the ghost's trajectory is bit-faithful to a human doing the same thing.
    private static float ToFloat(long raw) =>
        (float)(double)FixedQ4816.FromRawBits(value: raw);

    // The first free non-human slot (1..MaxPlayers-1), or a declared valid slot, or -1 when the roster is full.
    private static int ResolveSlot(HashSet<int> claimed, int? declared) {
        if ((declared is int slot) && (slot >= 1) && (slot < OverworldWorld.MaxPlayers)) {
            return slot;
        }

        for (var candidate = 1; (candidate < OverworldWorld.MaxPlayers); ++candidate) {
            if (!claimed.Contains(item: candidate)) {
                return candidate;
            }
        }

        return -1;
    }

    // A deterministic padless identity for an addon-driven ghost seated at a specific slot — distinct from the pad
    // roster's DeterministicGuid (0xA571...) and the scripted-player guid (0x5C81...) so a ghost never collides.
    private static Guid AddonPlayerId(int slot) {
        var bytes = new byte[16];

        _ = BitConverter.TryWriteBytes(destination: bytes, value: 0xADD0_0000u | (uint)slot);

        return new Guid(b: bytes);
    }

    // Logs one sanctioned-whimsy load line per addon (petname + hash for an enabled addon; the sticky fault detail for
    // one that loaded faulted). Diagnostics ride stderr like the demo's other [overworld]/[addon] lines.
    private static void Log(AddonHost host, string name, int slot) {
        if (!host.TryGet(instance: out var instance, name: name)) {
            return;
        }

        if (instance.State == AddonState.Enabled) {
            var petname = ContentPetname.From(hashHex: $"{instance.Hash.Value:x16}");

            Console.Error.WriteLine(value: $"[addon] loaded {petname} ({instance.Hash}) slot {SlotLabel(slot: slot)} fuel {instance.FuelPerTick}");
        } else {
            Console.Error.WriteLine(value: $"[addon] {name}: {instance.Fault.Detail}");
        }
    }
    private static string DescribeInstance(AddonInstance addon) {
        var petname = ContentPetname.From(hashHex: $"{addon.Hash.Value:x16}");
        var slot = ((addon.Slot is int value) ? $"{value}" : "-");

        return $"[addon {addon.Name}: {petname} {addon.Hash} slot {slot} fuel-last {addon.LastFuelConsumed} {StateLabel(addon: addon)}]";
    }
    private static string SlotLabel(int slot) =>
        ((slot >= 0) ? $"{slot}" : "unseated");
    private static string StateLabel(AddonInstance addon) {
        return addon.State switch {
            AddonState.Enabled => "ENABLED",
            AddonState.Disabled => "DISABLED",
            _ => $"FAULTED({addon.Fault.Kind})",
        };
    }
}
