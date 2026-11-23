using Puck.Commands;

namespace Puck.World;

/// <summary>
/// <c>world.iteration</c>: reads the edit loops <see cref="WorldIterationClock"/> times, and begins a named loop for an
/// edit a script is about to make.
/// </summary>
/// <param name="clock">The World's edit-loop clock.</param>
internal sealed class WorldIterationCommandModule(WorldIterationClock clock) : ICommandModule {
    private static readonly CommandForms Forms = CommandForms.Of(verb: "world.iteration", "[begin <name> [live]]");

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "world.iteration",
            description: $"Reads how long each edit loop took to show in the presented frames: {Forms.Spelling}. With no argument it answers one line for the boot (timed from process start) and one for each of the latest loops, oldest first: '#<n> <name> frame=<s> shaded=<s> shown=<s> meshes=<s> lit=<s> presented=<frames> builds=<programs> appearances=<packs> worst-frame=<ms> compiles=<n> compositions=<n> composition-members=<n> reload-rows-merged=<n> documents-read=<n> validations=<n>', each stage in seconds from the edit to the first produced frame that found it true. frame: a frame rendered the edited document or a reloaded kernel with the program its content calls for (a look value shows). shaded: every shading program the frame runs draws with its own kernel (an edited shading program shows once its specialization publishes, drawing as a material with no program until then). shown: every mesh group of that program draws its own geometry at some tier, no stand-in from before the edit and no hole (a changed form shows whole). meshes: the bakes settled at their demanded detail and stand in the mesh region (world.wait residency's fact). lit: the irradiance volume holds the edit's lighting and converged (the settled frame): nothing left to trace or relight, or under a lighting still changing every frame since the edit (a day clock), its geometry settled and its relight keeping every probe within its age bound. The first stage still open reads 'pending (<what it waits on>)'; presented counts the frames produced since the edit, builds the composition's programs that derived a field, appearances its rebuilds that packed a new appearance into the held field (a colour or material value; neither for an exposure or light edit), worst-frame the longest wait between produced frames until shaded, and the rest the source work the world.boot ledger counted from the edit to the settled frame. A loop that stopped early says why in 'ended=(...)': superseded by a later edit, a refused reload, or a kernel reload that changed nothing. world.reload, by hand or from world.watch, and world.shaders.reload each begin a loop; 'begin <name>' begins one for the edit on the next line, such as a world.row.set, and a reload or kernel reload that follows it joins it rather than beginning its own; 'begin <name> live' begins one for a live state edit (a world.state.cell.set: a clock scrubbed, a lamp's power), whose frame stage holds at the first frame presenting a tick after the one the loop began at, since no document moves. A settled loop also prints '[world.iteration] <line>' on stderr. world.wait iteration <seconds> holds a script until the latest loop settles. Refuses nothing; on a host that composes no renderer it says no loop reaches a frame.",
            forms: Forms,
            handler: (_, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: clock.Report());
                }
                var live = ((args.Count == 3) && args.Is(index: 2, value: "live"));

                if (((args.Count != 2) && !live) || !args.Is(index: 0, value: "begin")) {
                    return Forms.Refuse(args: in args);
                }

                var name = args[1].ToString();
                var number = clock.Begin(name: name, named: true, live: live);

                return new CommandResult(Output: $"[world.iteration: #{number} {name} begun]");
            }
        );
    }
}
