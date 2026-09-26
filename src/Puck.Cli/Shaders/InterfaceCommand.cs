using System.CommandLine;
using System.Text;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary><c>puck shaders interface</c>: prints or writes the frame-block declarations a pipeline's passes or an
/// engine package read through their generated interface, and optionally each interface's echo pass. A pipeline pass
/// compiles against its declarations without a file; an engine package's shaders compile at build, so their
/// declarations are written beside their sources and checked in.</summary>
internal static class InterfaceCommand {
    // Every interface the source names, with the directory its declarations resolve in.
    private static IReadOnlyList<(ShaderInterface Interface, string Directory)> InterfacesOf(string path) {
        var plan = RenderGraphCompiler.ShaderPasses.Compile(definition: ShaderPipelineLoader.ReadDefinition(
            name: Path.GetFileNameWithoutExtension(path: path),
            path: path
        )).Pipeline;
        var directory = Path.GetDirectoryName(path: path)!;
        var interfaces = new List<(ShaderInterface Interface, string Directory)>();

        foreach (var pass in plan.Passes) {
            var sourceDirectory = Path.GetDirectoryName(path: Path.GetFullPath(
                basePath: directory,
                path: pass.Declaration!.Source
            ))!;

            if (!interfaces.Any(predicate: entry => ((entry.Interface.Hash == pass.Parameters.Interface.Hash) && string.Equals(
                a: entry.Directory,
                b: sourceDirectory,
                comparisonType: StringComparison.Ordinal
            )))) {
                interfaces.Add(item: (pass.Parameters.Interface, sourceDirectory));
            }
        }

        return interfaces;
    }

    public static Command Create() {
        var source = new Argument<string>(name: "source") { Description = "The graph document or one-off shader source; with --package, the directory its shaders live in." };
        var package = new Option<string>(name: "--package") { Description = "The engine package (such as overlay or sdf.film-grain) whose declared interface to generate, rather than a document's." };
        var write = new Option<bool>(name: "--write") { Description = "Write each interface's declarations beside its source, as <interface>.interface.hlsli, rather than printing them." };
        var echo = new Option<bool>(name: "--echo") { Description = "Also generate each interface's echo pass, as <interface>.echo.hlsl." };
        var command = new Command(
            description: "Print or write the frame-block declarations a pipeline's passes or an engine package read, and their echo passes.",
            name: "interface"
        ) { source, package, write, echo };

        command.SetAction(action: result => {
            var path = Path.GetFullPath(path: result.GetRequiredValue(argument: source));
            var packageId = result.GetValue(option: package);
            RenderGraphPackage? declared = null;

            if (packageId is not null) {
                if (!Directory.Exists(path: path)) {
                    return CliExit.Refuse(
                        verb: "shaders interface",
                        what: path,
                        why: "no such directory."
                    );
                }
                if (!RenderGraphPackageCatalog.Engine.TryGet(
                    id: packageId,
                    package: out declared
                )) {
                    return CliExit.Refuse(
                        verb: "shaders interface",
                        what: packageId,
                        why: "no engine package has that id."
                    );
                }
            } else if (!File.Exists(path: path)) {
                return CliExit.Refuse(
                    verb: "shaders interface",
                    what: path,
                    why: "no such file."
                );
            }

            IReadOnlyList<(ShaderInterface Interface, string Directory)> interfaces;

            try {
                interfaces = ((declared is null)
                    ? InterfacesOf(path: path)
                    : [(ShaderPipelineParameterLayout.ForPackage(
                        config: declared.Config,
                        members: declared.Members,
                        package: declared.Id
                    ).Interface, path)]);
            } catch (Exception exception) when ((exception is InvalidDataException or System.Text.Json.JsonException or ShaderPipelineCompilationException or IOException)) {
                return CliExit.Refuse(
                    verb: "shaders interface",
                    what: CliPaths.ToDisplay(fullPath: path),
                    why: exception.Message.ReplaceLineEndings(replacementText: " ")
                );
            }

            foreach (var (shaderInterface, directory) in interfaces) {
                var files = new List<(string Path, string Text)> {
                    (Path.Combine(
                        path1: directory,
                        path2: ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name)
                    ), ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface)),
                };

                if (result.GetValue(option: echo)) {
                    files.Add(item: (Path.Combine(
                        path1: directory,
                        path2: (shaderInterface.Name + ".echo.hlsl")
                    ), ShaderInterfaceEcho.Generate(shaderInterface: shaderInterface)));
                }

                foreach (var (file, text) in files) {
                    if (result.GetValue(option: write)) {
                        File.WriteAllText(
                            contents: text,
                            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            path: file
                        );
                        Console.Out.WriteLine(value: $"shaders interface: wrote {CliPaths.ToDisplay(fullPath: file)}");
                    } else {
                        Console.Out.WriteLine(value: $"// {CliPaths.ToDisplay(fullPath: file)}");
                        Console.Out.Write(value: text);
                    }
                }
            }

            return CliExit.Success;
        });

        return command;
    }
}
