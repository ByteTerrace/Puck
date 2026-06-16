namespace Puck.Commands;

/// <summary>
/// Supplies a set of command definitions for aggregation by a <see cref="CommandRegistry"/>.
/// </summary>
/// <remarks>
/// Modules are the unit of composition for the command system: each module contributes its own
/// definitions, and the registry combines the definitions from every registered module.
/// </remarks>
public interface ICommandModule {
    /// <summary>Returns the command definitions provided by this module.</summary>
    /// <returns>The sequence of command definitions to register.</returns>
    IEnumerable<CommandDefinition> GetCommands();
}
