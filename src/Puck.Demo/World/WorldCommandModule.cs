using System.CommandLine;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Demo.World;

/// <summary>
/// The <c>world.*</c> console verbs — the precise/named half of the pad-first + console-assist input model (see
/// <see cref="WorldSculptController"/> for the pad half). Every verb forwards to <see cref="WorldCommands"/>, the
/// static class that carries the actual logic (the blessed CA1502/CA1506 escape this codebase already uses for
/// <c>Puck.Demo.Forge.ForgeCommands</c>), so this module itself stays a thin registration shim. The scene/history
/// are reached through <c>ICreatorModeHost.CreatorFrameSource</c> — the one host seam the render node already pays
/// the coupling for (its analyzer ceiling has NO headroom for a second interface; the frame source is every
/// authoring surface's composition point).
/// </summary>
internal sealed class WorldCommandModule(IRenderNode rootNode) : ICommandModule {
    private readonly Overworld.ICreatorModeHost? m_host = (rootNode as Overworld.ICreatorModeHost);

    private WorldScene? Scene => m_host?.CreatorFrameSource?.WorldScene;
    private EditHistory<WorldScene.Snapshot>? History => m_host?.CreatorFrameSource?.WorldHistory;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Plain(description: "Toggles world-sculpt mode (the town authoring surface; mutually exclusive with creator/tracker).", handler: _ => new CommandResult(m_host?.ToggleWorldSculptMode() ?? "[world: unavailable — the overworld is not the active root]"), name: "world");

        yield return Plain(description: "Lists the saved worlds under ./worlds/.", handler: _ => new CommandResult(WorldCommands.List()), name: "world.list");
        yield return WithArgs(description: "Loads a saved world by handle or path: world.load <name>.", handler: WithSceneArgs(handler: static (scene, store, args) => ((args.Length == 0) ? "[world.load: give a name — world.list shows what's saved]" : WorldCommands.Load(nameOrPath: args[0], scene: scene, store: store))), name: "world.load");
        yield return Plain(description: "Saves the live world (writes puck.world.v1 + a content-addressed object).", handler: WithScene(handler: static (scene, store) => WorldCommands.Save(scene: scene, store: store)), name: "world.save");
        yield return Plain(description: "Composes the world's theme song from the saved hash: world.theme (save first).", handler: WithScene(handler: static (scene, store) => WorldCommands.Theme(scene: scene, store: store)), name: "world.theme");
        yield return Plain(description: "Byte-compares the live model against the last saved copy.", handler: WithScene(handler: static (scene, _) => WorldCommands.Verify(scene: scene)), name: "world.verify");
        yield return WithArgs(description: "Arms the ghost with a creation and places it: world.place <creation> [countX countZ spacingX spacingZ].", handler: WithSceneArgs(handler: static (scene, store, args) => WorldCommands.Place(args: args, scene: scene, store: store)), name: "world.place");
        yield return WithArgs(description: "Selects a placement by id: world.select <id>.", handler: WithSceneArgs(handler: static (scene, _, args) => WorldCommands.Select(args: args, scene: scene)), name: "world.select");
        yield return Plain(description: "Deletes the selected placement.", handler: WithScene(handler: static (scene, _) => WorldCommands.Delete(scene: scene)), name: "world.del");
        yield return WithArgs(description: "Moves the target to an exact position: world.move <x> <y> <z>.", handler: WithSceneArgs(handler: static (scene, _, args) => WorldCommands.Move(args: args, scene: scene)), name: "world.move");
        yield return WithArgs(description: "Sets the world's movement direction lock: world.movement <free|four|eight|hex>.", handler: WithSceneArgs(handler: (scene, _, args) => WorldCommands.Movement(args: args, history: History, scene: scene)), name: "world.movement");
        yield return WithArgs(description: "The daylight dial: world.dusk (evening), world.dusk <0.15..1>, world.dusk day. Lamps glow when the room dims.", handler: WithSceneArgs(handler: static (scene, _, args) => WorldCommands.Dusk(args: args, scene: scene)), name: "world.dusk");
        yield return WithArgs(description: "Sets the target's yaw: world.rotate <degrees>.", handler: WithSceneArgs(handler: static (scene, _, args) => WorldCommands.Rotate(args: args, scene: scene)), name: "world.rotate");
        yield return WithArgs(description: "Sets the target's uniform scale: world.scale <factor>.", handler: WithSceneArgs(handler: static (scene, _, args) => WorldCommands.Scale(args: args, scene: scene)), name: "world.scale");
        yield return WithArgs(description: "Sets/clears the selected placement's repeat: world.repeat <countX> <countZ> [spacingX spacingZ], or world.repeat clear.", handler: WithSceneArgs(handler: static (scene, _, args) => WorldCommands.Repeat(args: args, scene: scene)), name: "world.repeat");
        yield return WithArgs(description: "Grows/shrinks the authored lot bounds: world.bounds <±x> <±z>.", handler: WithSceneArgs(handler: static (scene, _, args) => WorldCommands.Bounds(args: args, scene: scene)), name: "world.bounds");
        yield return WithArgs(description: "Rebinds the selected placement to a different creation: world.rebind <creation>.", handler: WithSceneArgs(handler: static (scene, store, args) => WorldCommands.Rebind(args: args, scene: scene, store: store)), name: "world.rebind");
        yield return WithArgs(description: "Opens a placement by id for a console-narrated edit session: world.edit <placement>.", handler: WithSceneArgs(handler: static (scene, _, args) => WorldCommands.Edit(args: args, scene: scene)), name: "world.edit");
        yield return WithArgs(description: "Sets/clears the selected placement's mirror fold: world.mirror <x|z|off>.", handler: WithSceneArgs(handler: (scene, _, args) => WorldCommands.Mirror(args: args, history: History, scene: scene)), name: "world.mirror");
        yield return WithArgs(description: "Sets/clears the selected placement's wallpaper pattern: world.pattern <group> <cellW> <cellH> [limitX limitZ] [stride], or world.pattern off.", handler: WithSceneArgs(handler: (scene, _, args) => WorldCommands.Pattern(args: args, history: History, scene: scene)), name: "world.pattern");
        yield return WithArgs(description: "Sets the walk grid tessellation the next save bakes: world.grid <square|hex>.", handler: WithSceneArgs(handler: (scene, _, args) => WorldCommands.Grid(args: args, history: History, scene: scene)), name: "world.grid");
        yield return WithArgs(description: "Places/deletes/lists diegetic camera eyes: world.camera add [x y z] [yaw°] [pitch°] | del <id> | list.", handler: WithSceneArgs(handler: (scene, _, args) => WorldCommands.Camera(args: args, history: History, scene: scene)), name: "world.camera");
        yield return WithArgs(description: "Wires a screen to a source: world.wire <brick:N|feed:N|named:NAME|none> <screen> | world.wire list | world.wire clear <screen>.", handler: WithSceneArgs(handler: (scene, _, args) => WorldCommands.Wire(args: args, history: History, scene: scene)), name: "world.wire");
    }

    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    private CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Description: description,
            Handler: context => handler(arg1: context, arg2: (context.Parse?.GetValue(argument: rest) ?? [])),
            Name: name,
            TextCommand: new Command(description: description, name: name) {
                rest,
            },
            ValueKind: CommandValueKind.Digital
        );
    }

    // Wraps a scene-reading handler with the availability guard (the world sculptor's host must be the active root).
    private Func<CommandContext, CommandResult> WithScene(Func<WorldScene, Puck.Assets.ContentAddressedStore, string> handler) {
        return _ => {
            if (Scene is not { } scene) {
                return new CommandResult("[world: unavailable — the overworld is not the active root]");
            }

            return new CommandResult(handler(arg1: scene, arg2: WorldCommands.OpenStore()));
        };
    }

    private Func<CommandContext, string[], CommandResult> WithSceneArgs(Func<WorldScene, Puck.Assets.ContentAddressedStore, string[], string> handler) {
        return (_, args) => {
            if (Scene is not { } scene) {
                return new CommandResult("[world: unavailable — the overworld is not the active root]");
            }

            return new CommandResult(handler(arg1: scene, arg2: WorldCommands.OpenStore(), arg3: args));
        };
    }
}
