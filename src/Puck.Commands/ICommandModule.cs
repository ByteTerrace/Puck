namespace Puck.Commands;

/// <summary>
/// Supplies a set of command definitions for aggregation by a <see cref="CommandRegistry"/>.
/// </summary>
/// <remarks>
/// <para>Modules are the unit of composition for the command system: each module contributes its own
/// definitions, and the registry combines the definitions from every registered module.</para>
/// <para><b>The convention.</b> A stateful module names the state it drives as constructor parameters and
/// keeps its verb logic inline — the shape every module in <c>Puck.World</c> and <c>Puck.Launcher</c> takes.
/// Do not reach for <see cref="IServiceProvider"/>: a module that would need one is a module whose
/// dependencies have not been named, and no module in the tree does it.</para>
/// <para>When a module's own member count or coupling would cross the analyzer ceiling (CA1502/CA1506),
/// carve it by SUBJECT into more modules rather than splitting one module into a thin registration shell
/// plus a static <c>*Commands</c> logic class — the editor's six <c>EditorSculpt*CommandModule</c>s are that
/// carve. The shell/logic split was the older escape and no module uses it today.</para>
/// </remarks>
public interface ICommandModule {
    /// <summary>Returns the command definitions provided by this module.</summary>
    /// <returns>The sequence of command definitions to register.</returns>
    IEnumerable<CommandDefinition> GetCommands();
}
