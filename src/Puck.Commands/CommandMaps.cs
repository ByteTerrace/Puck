namespace Puck.Commands;

/// <summary>
/// Defines well-known command-map names.
/// </summary>
/// <remarks>
/// A command map is a named group of commands that can be activated or deactivated together,
/// providing the modality model that determines which commands accept source-driven activation at
/// any given time. Hosts define their own maps, such as gameplay, console, or menu, in addition to
/// <see cref="Global"/>.
/// </remarks>
public static class CommandMaps {
    /// <summary>The name of the map that is always active and cannot be deactivated.</summary>
    public const string Global = "Global";
}
