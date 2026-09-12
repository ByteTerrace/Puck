using System.CommandLine;

namespace Puck.Cli.Shaders;

/// <summary><c>puck shaders</c> — shader authoring verbs that sit outside the ordinary build-time
/// <c>puck.shader.v1</c> recipe.</summary>
internal static class ShadersCommand {
    public static Command Create() {
        var command = new Command(description: "Shader authoring verbs.", name: "shaders");

        command.Subcommands.Add(item: CompileShaderCommand.Create());
        command.Subcommands.Add(item: PipelineCommand.Create());
        return command;
    }
}
