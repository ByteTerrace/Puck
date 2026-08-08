using System.Globalization;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Launcher;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The host-section READ-BACK — <c>world.host</c>, the three-way read (DOCUMENT row, RESOLVED boot values, LIVE
/// lever values). This module WRITES nothing: the section is authored through
/// <c>world.row.set host &lt;json&gt;</c> — a single always-resolving row, so there is no separate insert path
/// either. A SEPARATE module from <see cref="WorldMutationCommandModule"/> because the
/// <c>world.host</c> read needs <see cref="PresentPacingControl"/> and <see cref="GpuTimingControl"/> (the two live
/// levers), which would push that class past its analyzer ceiling.
/// </summary>
/// <remarks>The host section is DOCUMENT-DEFAULTS class: <c>world.row.set host</c> moves the DOCUMENT (next boot for
/// the boot-only fields; the value the next boot wakes on for the two live-lever fields), never the live levers —
/// <c>world.target</c> / <c>world.timing</c> own those. <c>world.host</c>'s three columns make the split visible:
/// which fields the CLI overrode (DOCUMENT vs RESOLVED) and which levers have drifted (DOCUMENT vs LIVE).</remarks>
internal sealed class WorldHostCommandModule(WorldServer server, WorldHostSettings hostSettings, PresentPacingControl pacing) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.host",
            description: "Reads the host section three ways (Immediate): the DOCUMENT row (the authored host defaults, absence coalesced to the built-in default), the RESOLVED boot values (the document overlaid by the CLI window/backend flags), and the LIVE lever values (world.target's present Hz + world.timing's armed state) — so an author sees which fields the CLI overrode and which levers have drifted.",
            handler: (_, _) => new CommandResult(Output: DescribeHost())
        );
    }

    // The three-way read-back: DOCUMENT (the coalesced authored row), RESOLVED (the boot values after CLI override), and
    // LIVE (the two session levers). One line, pipe-separated, so it stays greppable over stdout.
    private string DescribeHost() {
        var document = server.Definition.Host;
        var targetRate = ((pacing.TargetHertz > 0.0) ? pacing.TargetHertz.ToString(provider: CultureInfo.InvariantCulture) : "display");

        return (((((($"[world.host: document {{{DescribeRow(host: document)}}} " +
            $"resolved {{backend={WorldHostTokens.BackendToken(backend: (hostSettings.HostsOnDirectX ? WorldBackendPreference.DirectX : WorldBackendPreference.Vulkan))} ") +
            $"width={hostSettings.Width} height={hostSettings.Height} surfaceFormat={WorldHostTokens.SurfaceFormatToken(format: hostSettings.SurfaceFormat)} ") +
            $"fullscreen={Bool(value: hostSettings.Fullscreen)} presentMode={PresentModeToken(mode: hostSettings.PresentMode)} ") +
            $"targetHertz={HertzToken(hertz: hostSettings.TargetHertz)} exitAfterSeconds={hostSettings.ExitAfterSeconds} ") +
            $"rayQuery={Bool(value: hostSettings.RayQuery)} timing={Bool(value: hostSettings.Timing)} genlock={Genlock(value: hostSettings.Genlock)}}} ") +
            $"live {{targetHertz={targetRate} timing={(GpuTimingControl.Shared.Armed ? "on" : "off")}}}]");
    }
    private static string DescribeRow(WorldHostDefaults host) =>
        ((($"backend={((host.BackendDraw is not null) ? "<draw>" : WorldHostTokens.BackendToken(backend: (host.Backend ?? WorldBackendPreference.Auto)))} width={host.Width} height={host.Height} " +
        $"surfaceFormat={WorldHostTokens.SurfaceFormatToken(format: host.SurfaceFormat)} fullscreen={Bool(value: host.Fullscreen)} ") +
        $"presentMode={PresentModeToken(mode: host.PresentMode)} targetHertz={HertzToken(hertz: host.TargetHertz)} ") +
        $"exitAfterSeconds={host.ExitAfterSeconds} rayQuery={Bool(value: host.RayQuery)} timing={Bool(value: host.Timing)} genlock={Genlock(value: host.Genlock)}");
    private static string Bool(bool value) => (value ? "true" : "false");
    private static string HertzToken(double hertz) => hertz.ToString(provider: CultureInfo.InvariantCulture);
    private static string Genlock(string? value) => (value ?? "(none)");
    private static string PresentModeToken(PresentMode mode) => mode.ToString().ToLowerInvariant();
}
