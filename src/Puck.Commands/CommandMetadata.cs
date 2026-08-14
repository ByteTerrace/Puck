namespace Puck.Commands;

/// <summary>
/// The publicly readable facts about one registered command: what it is called, what it carries, how a submitted line
/// routes, and whether a binding document may name it. This is what <see cref="CommandRegistry.Definitions"/> hands
/// out — the affordance manifest and the binding vocabulary read exactly these.
/// </summary>
/// <remarks>Metadata, and only metadata: the invocable <see cref="CommandDefinition.Handler"/> stays internal to this
/// assembly. A caller that could reach a handler could invoke an authority verb with a <see cref="CommandContext"/> of
/// its own making, which is a dispatch door beside the stamped ones — describing a command must never be the same act
/// as being able to run it.</remarks>
/// <param name="Name">The command's canonical name.</param>
/// <param name="ValueKind">The shape of the value the command carries.</param>
/// <param name="Routing">Whether a submitted text line runs inline or folds into the deterministic snapshot stream.</param>
/// <param name="Bindability">Whether a binding document may name this command as a destination.</param>
/// <param name="InputScope">Whether source-driven activation requires ordinary terminal focus.</param>
/// <param name="Map">The command map that classifies source-driven activation.</param>
public readonly record struct CommandMetadata(
    string Name,
    CommandValueKind ValueKind,
    CommandRouting Routing,
    CommandBindability Bindability,
    CommandInputScope InputScope = CommandInputScope.Focused,
    string Map = CommandMaps.Global
);
