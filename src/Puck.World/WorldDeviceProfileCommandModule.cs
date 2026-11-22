using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// <c>world.device-profile</c>: selects the device profile (<see cref="WorldDeviceProfile"/>) the world authors for the
/// adapter the process runs on, or the one named, and applies it: the quality table (the profile's own, or the world's
/// preset of its tier) through the render levers where a renderer is composed, and the memory bars to the process account.
/// A windowed or offscreen boot selects by adapter once its device opens and names its adapter, on the frame thread
/// before its first frame stages anything (<see cref="WorldDeviceProfileState.ApplyAtBoot"/>), through the same door
/// (<see cref="WorldDeviceProfileState.Select"/>), so every device boots on its own table; a benchmark run applies its
/// named profile before its first chapter.
/// </summary>
internal sealed class WorldDeviceProfileCommandModule(WorldServer server, WorldDeviceProfileState state, WorldRenderProbe? renderProbe = null, IWorldQualityPresets? presets = null) : ICommandModule {
    private static readonly CommandForms DeviceProfileForms = CommandForms.Of(verb: "world.device-profile", "[auto|none|<name>]");

    private string Echo() => state.Echo();

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

                return state.Select(adapter: adapter, presets: presets, principal: context.Principal, profile: profile, render: render, source: source);
            }
        );
    }
}
