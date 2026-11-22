using System.Globalization;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting.Jobs;
using Puck.Abstractions.Memory;

namespace Puck.World;

/// <summary>Applies a world's quality preset through the render levers; implemented by the module that owns them, so the
/// presets <c>world.quality</c> writes and the table a device profile names are one path.</summary>
public interface IWorldQualityPresets {
    /// <summary>Writes a preset's levers into the live render settings.</summary>
    /// <param name="principal">The principal the lever submissions act as.</param>
    /// <param name="tier">The tier the preset is authored under.</param>
    /// <param name="preset">The preset.</param>
    /// <param name="table">The name the preset is found by again: a tier's name for a tier's preset, a device profile's
    /// for its own table (<see cref="WorldDeviceProfile.TableName"/>), which the presentation reads the preset's nested-view
    /// table by (<see cref="WorldRenderSettings.NestedTable"/>).</param>
    /// <returns>The <c>world.quality</c> echo of the settings the preset left.</returns>
    CommandResult Apply(Principal principal, QualityTier tier, WorldQualityPreset preset, string table);
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
    /// <summary>The table a selection names while its levers are being written (<see cref="Select"/>).</summary>
    public const string PendingTable = "pending";

    /// <summary>Applies the profile a boot selects by its adapter once the device has named it, on the frame thread
    /// before its first frame stages anything, unless a selection was already made, which a boot never overrides: its
    /// memory bars and then its table, through <see cref="Select"/>, the one door <c>world.device-profile</c> takes too.
    /// The table is written directly, never as a queued console line, so no hold on a console session can delay or drop
    /// it.</summary>
    /// <param name="profile">The profile the world authors for the adapter, or <see langword="null"/> for none.</param>
    /// <param name="adapter">The adapter's name.</param>
    /// <param name="render">The world's render defaults, which hold the profile's table.</param>
    /// <param name="presets">The door the table's levers are written through, or <see langword="null"/> where no renderer
    /// is composed.</param>
    /// <returns>The <c>world.device-profile</c> echo of what applied, or <see langword="null"/> when a selection was
    /// already made.</returns>
    public CommandResult? ApplyAtBoot(WorldDeviceProfile? profile, string? adapter, WorldRenderDefaults render, IWorldQualityPresets? presets) {
        lock (m_gate) {
            if (m_current != Selection.None) { return null; }
            _ = ApplyLocked(adapter: adapter, profile: profile, source: "auto", table: null);
        }

        return ((profile is null)
            ? new CommandResult(Output: Echo())
            : Select(adapter: adapter, presets: presets, principal: Principal.Console, profile: profile, render: render, source: "auto"));
    }
    /// <summary>Applies a profile: its memory bars, then its quality table (its own, or the world's preset of its tier)
    /// through the presets where a renderer is composed. The selection reads <see cref="PendingTable"/> until the
    /// table's levers have been written, then the table's name.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="source">How it was selected.</param>
    /// <param name="adapter">The adapter's name, or <see langword="null"/> where no device is open.</param>
    /// <param name="render">The world's render defaults, which hold the profile's table.</param>
    /// <param name="principal">The principal the table's levers act as.</param>
    /// <param name="presets">The door the table's levers are written through, or <see langword="null"/>.</param>
    /// <returns>The <c>world.device-profile</c> echo, or the presets' refusal, after which the selection names no
    /// table.</returns>
    public CommandResult Select(WorldDeviceProfile profile, string source, string? adapter, WorldRenderDefaults render, Principal principal, IWorldQualityPresets? presets) {
        var preset = ((presets is null) ? null : profile.TableIn(render: render));

        _ = Apply(adapter: adapter, profile: profile, source: source, table: ((preset is null) ? null : PendingTable));
        if (preset is not { } table) {
            return new CommandResult(Output: Echo());
        }

        var result = presets!.Apply(principal: principal, preset: table, table: profile.TableName, tier: profile.Quality);

        _ = Apply(adapter: adapter, profile: profile, source: source, table: (result.IsError ? null : profile.TableName));

        return (result.IsError ? result : new CommandResult(Output: Echo()));
    }
    /// <summary>Gets the <c>world.device-profile</c> echo of the current selection.</summary>
    /// <returns>The echo line.</returns>
    public string Echo() => $"[world.device-profile: {Current.Describe()}]";

    // Under m_gate, so a boot's selection and a command's are applied one at a time and the recorded selection is the
    // one whose bars the account holds.
    private Selection ApplyLocked(WorldDeviceProfile? profile, string source, string? adapter, string? table) {
        MemoryAccount.Shared.SetBudget(bytes: profile?.DeviceMemoryBytes, pool: PhysicalMemory.Device);
        MemoryAccount.Shared.SetHostBar(bytes: profile?.HostMemoryBytes);

        var selection = new Selection(Profile: profile?.Name, Source: source, Adapter: adapter, Table: table, DeviceMemoryBytes: profile?.DeviceMemoryBytes, HostMemoryBytes: profile?.HostMemoryBytes);

        m_current = selection;

        return selection;
    }
}
