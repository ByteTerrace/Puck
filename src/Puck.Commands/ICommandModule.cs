namespace Puck.Commands;

/// <summary>
/// Supplies a set of command definitions for aggregation by a <see cref="CommandRegistry"/>.
/// </summary>
/// <remarks>
/// <para>Modules are the unit of composition for the command system: each module contributes its own
/// definitions, and the registry combines the definitions from every registered module.</para>
/// <para><b>The Puck.Demo convention (constructor + module/logic-class split).</b> A stateful module's
/// constructor should take its target's owning <c>IRenderNode</c> (the demo's render-node root) and reach the
/// target through the render node's host interface (e.g. <c>ICreatorModeHost.CreatorFrameSource</c>) — this is
/// the default, used by every stateful module except one (see below). Reach for <see cref="IServiceProvider"/>
/// instead ONLY when the target's state cannot be added to the render node's host interface without exceeding
/// its analyzer coupling ceiling (CA1502/CA1506) — document the specific ceiling in a remarks block when you do
/// this, the way <c>Puck.Demo.Tracker.TrackerCommandModule</c> does; it is the one deliberate exception.</para>
/// <para>Split the module into a thin <c>*CommandModule</c> (registration + the availability-guard wrappers) plus
/// a separate static <c>*Commands</c> logic class ONLY when the module's own member count/complexity would
/// otherwise cross the same CA1502/CA1506 ceiling (see <c>Puck.Demo.World.WorldCommandModule</c> +
/// <c>WorldCommands</c>, or <c>Puck.Demo.Forge.ForgeCommands</c>) — the split is an analyzer escape, not a
/// default shape; a module small enough to stay under ceiling should keep its verb logic inline (see
/// <c>Puck.Demo.Tracker.TrackerCommandModule</c>, <c>Puck.Demo.Creator.CompanionCommandModule</c>). A *Commands
/// class that exists purely to re-carve an oversized module into more files without adding independent logic
/// (rather than to host logic itself) is a *registration* helper, not a logic class — <c>CreatorRigCommands</c>
/// is that shape; it still reuses the owning module's guard/parse wrappers rather than re-deriving them.</para>
/// </remarks>
public interface ICommandModule {
    /// <summary>Returns the command definitions provided by this module.</summary>
    /// <returns>The sequence of command definitions to register.</returns>
    IEnumerable<CommandDefinition> GetCommands();
}
