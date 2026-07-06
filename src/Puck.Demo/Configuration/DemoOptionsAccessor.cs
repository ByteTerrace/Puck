using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Puck.Demo.Configuration;

/// <summary>
/// The overworld render node's window onto <see cref="DemoOptions"/>: it resolves the bound options from the container
/// and exposes each headless-aid value as a primitive (int/long/bool/string/array). It is an ACCESSOR, not a
/// settings/config-bound POCO — it owns no state of its own and holds nothing that binds from a configuration
/// section (that's <see cref="DemoOptions"/>/<see cref="ScenarioOptions"/>; see <see cref="DemoConfiguration"/>'s
/// doc comment for the naming rule). The node routes every one of its former
/// <c>Environment.GetEnvironmentVariable("PUCK_OVERWORLD_*")</c> reads through here, so the node names ONE type
/// (this class) instead of <see cref="Environment"/> + <c>IOptions</c> + <see cref="DemoOptions"/> — keeping the node
/// at its class-coupling ceiling while the config plumbing lives here. Parsing of the comma-separated
/// <c>DebugBoot</c> list also lives here (the node just consumes the resulting sorted array).
/// </summary>
internal static class DemoOptionsAccessor {
    private static DemoOptions Resolve(IServiceProvider services) {
        return (services.GetService<IOptions<DemoOptions>>()?.Value ?? new DemoOptions());
    }

    /// <summary>The saved creation to load into the scene at boot (name or path), or null. Maps <c>PUCK_CREATOR_LOAD</c>.</summary>
    public static string? CreatorLoad(IServiceProvider services) {
        var value = Resolve(services: services).CreatorLoad;

        return (string.IsNullOrEmpty(value: value) ? null : value);
    }

    /// <summary>Whether to open straight into creator mode at boot. Maps <c>PUCK_OVERWORLD_CREATOR</c>.</summary>
    public static bool StartInCreator(IServiceProvider services) {
        return Resolve(services: services).Creator;
    }

    /// <summary>The active scenario's creation to load into creator mode (name or path), or null when no
    /// <c>--scenario</c> is active. Takes precedence over <see cref="CreatorLoad"/>.</summary>
    public static string? ScenarioCreation(IServiceProvider services) {
        var scenario = (services.GetService<IOptions<ScenarioOptions>>()?.Value ?? new ScenarioOptions());

        return ((scenario.Active && !string.IsNullOrEmpty(value: scenario.Creation)) ? scenario.Creation : null);
    }

    /// <summary>The scripted debug-player count, clamped to the player slots. Maps <c>PUCK_OVERWORLD_DEBUG_PLAYERS</c>.</summary>
    public static int DebugPlayerCount(IServiceProvider services, int maxPlayers) {
        return Math.Clamp(value: Resolve(services: services).DebugPlayers, min: 0, max: maxPlayers);
    }

    /// <summary>The far spawn cell (both axes). Maps <c>PUCK_OVERWORLD_CELL</c>.</summary>
    public static long SpawnCell(IServiceProvider services) {
        return Resolve(services: services).Cell;
    }

    /// <summary>The delayed one-shot capture frame, or -1. Maps <c>PUCK_OVERWORLD_CAPTURE_FRAME</c>.</summary>
    public static int CaptureFrame(IServiceProvider services) {
        return Resolve(services: services).CaptureFrame;
    }

    /// <summary>The world-lens boot cart type override (clamped to the valid range). Maps <c>PUCK_OVERWORLD_CART</c>.</summary>
    public static int WorldLensCartType(IServiceProvider services, int cartTypeCount) {
        var value = Resolve(services: services).Cart;

        return (((value >= 0) && (value < cartTypeCount)) ? value : 0);
    }

    /// <summary>Whether to fake a linked (0,1) console pair. Maps <c>PUCK_LINK_CABLE_PROBE</c>.</summary>
    public static bool LinkCableProbe(IServiceProvider services) {
        return Resolve(services: services).LinkCableProbe;
    }

    /// <summary>The sorted scripted console-boot ticks parsed from the comma-separated list. Maps
    /// <c>PUCK_OVERWORLD_DEBUG_BOOT</c>.</summary>
    public static ulong[] BootTicks(IServiceProvider services) {
        var raw = Resolve(services: services).DebugBoot;

        if (string.IsNullOrWhiteSpace(value: raw)) {
            return [];
        }

        var parts = raw.Split(separator: ',', options: (StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var ticks = new List<ulong>(capacity: parts.Length);

        foreach (var part in parts) {
            if (ulong.TryParse(s: part, result: out var tick)) {
                ticks.Add(item: tick);
            }
        }

        ticks.Sort();

        return [.. ticks];
    }
}
