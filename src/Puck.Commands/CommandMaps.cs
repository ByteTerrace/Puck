namespace Puck.Commands;

/// <summary>
/// Defines well-known command-map names.
/// </summary>
/// <remarks>
/// A command map is an immutable classification of commands. Each <see cref="InputRouter"/> resolves an independent
/// active set per logical slot, providing the modality model that determines which commands accept source-driven
/// activation for that player. Hosts define their own maps, such as gameplay, vehicle, plan, or menu, in addition to
/// <see cref="Global"/>.
/// </remarks>
public static class CommandMaps {
    /// <summary>The name of the map that is always active and cannot be deactivated.</summary>
    public const string Global = "Global";
}
