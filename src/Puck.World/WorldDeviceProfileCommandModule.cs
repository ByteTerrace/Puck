using System.Globalization;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting.Jobs;
using Puck.Abstractions.Memory;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Applies a world's quality preset through the render levers; implemented by the module that owns them, so the
/// presets <c>world.quality</c> writes and the table a device profile names are one path.</summary>
internal interface IWorldQualityPresets {
    /// <summary>Writes a preset's levers into the live render settings.</summary>
    /// <param name="context">The command context whose principal the lever submissions act as.</param>
    /// <param name="tier">The tier the preset is authored under.</param>
    /// <param name="preset">The preset.</param>
    /// <returns>The <c>world.quality</c> echo of the settings the preset left.</returns>
    CommandResult Apply(CommandContext context, QualityTier tier, WorldQualityPreset preset);
}
/// <summary>What the process runs as: the device profile selected for it, how it was selected, the adapter it ran on and
/// the memory bars it applied. One per process, read by <c>world.device-profile</c> and by a benchmark's report.</summary>
public sealed class WorldDeviceProfileState {
    private readonly Lock m_gate = new();

    private Selection m_current = Selection.None;

    /// <summary>One selection: a profile or none, how it came about, and what it applied.</summary>
    /// <param name="Profile">The profile's name, or <see langword="null"/> for none.</param>
    /// <param name="Source">How it was selected: <c>auto</c> (by the adapter), <c>named</c> (by <c>world.device-profile</c>
    /// or a benchmark run) or <c>none</c>.</param>
    /// <param name="Adapter">The adapter's name, or <see langword="null"/> where no device is open.</param>
    /// <param name="Table">The name of the table the profile applied (<see cref="WorldDeviceProfile.TableName"/>: its own
    /// name for a table it authors, its tier's for the world's preset), or <see langword="null"/> where it applied none.</param>
    /// <param name="DeviceMemoryBytes">The device pool's cap, or <see langword="null"/> for none.</param>
    /// <param name="HostMemoryBytes">The host pool's cap, or <see langword="null"/> for none.</param>
    public readonly record struct Selection(string? Profile, string Source, string? Adapter, string? Table, long? DeviceMemoryBytes, long? HostMemoryBytes) {
        /// <summary>Gets the selection of a process that selected nothing.</summary>
        public static Selection None { get; } = new(Profile: null, Source: "none", Adapter: null, Table: null, DeviceMemoryBytes: null, HostMemoryBytes: null);

        /// <summary>Gets the selection as one echo line body: <c>profile=… source=… adapter=… table=… device-memory-cap=…
        /// host-memory-cap=…</c>, a missing value spelled <c>none</c>.</summary>
        public string Describe() => string.Create(CultureInfo.InvariantCulture, $"profile={Profile ?? "none"} source={Source} adapter={Adapter ?? "none"} table={Table ?? "none"} device-memory-cap={Mebibytes(bytes: DeviceMemoryBytes)} host-memory-cap={Mebibytes(bytes: HostMemoryBytes)}");

        private static string Mebibytes(long? bytes) => (bytes is { } value) ? $"{(value >> 20)}MiB" : "none";
    }

    /// <summary>Gets the current selection.</summary>
    public Selection Current {
        get {
            lock (m_gate) { return m_current; }
        }
    }

    /// <summary>Applies a profile's memory bars to the process account and records the selection: each pool's budget is
    /// capped at the profile's figure, or restored to the one it derives where the profile states none.</summary>
    /// <param name="profile">The profile, or <see langword="null"/> to restore both pools.</param>
    /// <param name="source">How the profile was selected.</param>
    /// <param name="adapter">The adapter's name, or <see langword="null"/> where no device is open.</param>
    /// <param name="table">The name of the table the caller applied for the profile, or <see langword="null"/>.</param>
    /// <returns>The recorded selection.</returns>
    public Selection Apply(WorldDeviceProfile? profile, string source, string? adapter, string? table) {
        lock (m_gate) { return ApplyLocked(adapter: adapter, profile: profile, source: source, table: table); }
    }
    /// <summary>Applies the profile a boot selects by its adapter once the device has named it: its memory bars, as
    /// <see cref="Apply"/> applies them with the source <c>auto</c>, unless a selection was already made, which a boot
    /// never overrides. The caller then applies the profile's table through <c>world.device-profile auto</c>, the one
    /// path its levers take, when this returns <see langword="true"/>.</summary>
    /// <param name="profile">The profile the world authors for the adapter, or <see langword="null"/> for none.</param>
    /// <param name="adapter">The adapter's name.</param>
    /// <returns>Whether the boot selected a profile, so its table is still to apply.</returns>
    public bool ApplyAtBoot(WorldDeviceProfile? profile, string? adapter) {
        lock (m_gate) {
            if (m_current != Selection.None) { return false; }
            _ = ApplyLocked(adapter: adapter, profile: profile, source: "auto", table: null);

            return (profile is not null);
        }
    }

    // Under m_gate, so a boot's selection and a command's are applied one at a time and the recorded selection is the
    // one whose bars the account holds.
    private Selection ApplyLocked(WorldDeviceProfile? profile, string source, string? adapter, string? table) {
        MemoryAccount.Shared.SetBudget(bytes: profile?.DeviceMemoryBytes, pool: PhysicalMemory.Device);
        MemoryAccount.Shared.SetBudget(bytes: profile?.HostMemoryBytes, pool: PhysicalMemory.Host);

        var selection = new Selection(Profile: profile?.Name, Source: source, Adapter: adapter, Table: table, DeviceMemoryBytes: profile?.DeviceMemoryBytes, HostMemoryBytes: profile?.HostMemoryBytes);

        m_current = selection;

        return selection;
    }
}
/// <summary>
/// <c>world.device-profile</c>: selects the device profile (<see cref="WorldDeviceProfile"/>) the world authors for the
/// adapter the process runs on, or the one named, and applies it: the quality table (the profile's own, or the world's
/// preset of its tier) through the render levers where a renderer is composed, and the memory bars to the process account.
/// A windowed or offscreen boot selects by adapter once its device opens and names its adapter: it binds the memory bars
/// before its first frame stages anything (<see cref="WorldDeviceProfileState.ApplyAtBoot"/>) and then submits
/// <c>world.device-profile auto</c>, so every device boots on its own table; a benchmark run applies its named profile
/// before its first chapter.
/// </summary>
internal sealed class WorldDeviceProfileCommandModule(WorldServer server, WorldDeviceProfileState state, WorldRenderProbe? renderProbe = null, IWorldQualityPresets? presets = null) : ICommandModule {
    private static readonly CommandForms DeviceProfileForms = CommandForms.Of(verb: "world.device-profile", "[auto|none|<name>]");

    private string Echo() => $"[world.device-profile: {state.Current.Describe()}]";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "world.device-profile",
            forms: DeviceProfileForms,
            description: $"Selects a device profile from the world's render.devices and applies it: {DeviceProfileForms.Spelling}. A name applies that profile; auto selects by the adapter's name and, with no match, applies none; none restores the machine-derived memory budgets. Applying writes the profile's quality table through the same levers world.quality writes (where a renderer is composed) and caps the device pool and the host pool at the profile's memory bars, each only where lower than the pool's own limit. No argument echoes the selection: profile, how it was selected, the adapter, the table and the caps.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: Echo());
                }
                if (args.Count != 1) {
                    return DeviceProfileForms.Refuse(args: in args);
                }

                var render = server.ReadView(context: context).Definition.Render;
                var adapter = renderProbe?.DeviceIdentity?.AdapterName;
                var request = args[0].ToString();
                WorldDeviceProfile? profile;
                string source;

                if (string.Equals(a: request, b: "none", comparisonType: StringComparison.Ordinal)) {
                    _ = state.Apply(profile: null, source: "none", adapter: adapter, table: null);

                    return new CommandResult(Output: Echo());
                }
                if (string.Equals(a: request, b: "auto", comparisonType: StringComparison.Ordinal)) {
                    profile = WorldDeviceProfiles.Select(render: render, adapterName: adapter);
                    source = "auto";
                } else {
                    profile = WorldDeviceProfiles.Find(render: render, name: request);
                    source = "named";
                    if (profile is null) {
                        return CommandResult.Error(output: $"[world.device-profile: this world authors no profile named '{request}' (authored: {string.Join(separator: ", ", values: (render.Devices ?? []).Select(selector: static row => row.Name))})]");
                    }
                }
                if (profile is null) {
                    _ = state.Apply(profile: null, source: source, adapter: adapter, table: null);

                    return new CommandResult(Output: Echo());
                }

                string? applied = null;

                if ((presets is not null) && (profile.TableIn(render: render) is { } preset)) {
                    var result = presets.Apply(context: context, tier: profile.Quality, preset: preset);

                    if (result.IsError) { return result; }
                    applied = profile.TableName;
                }
                _ = state.Apply(profile: profile, source: source, adapter: adapter, table: applied);

                return new CommandResult(Output: Echo());
            }
        );
    }
}
