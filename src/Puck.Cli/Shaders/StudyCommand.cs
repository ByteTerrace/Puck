using System.CommandLine;

using Puck.Shaders.Study;

namespace Puck.Cli.Shaders;

/// <summary><c>puck shaders study</c> — compiles one Shadertoy-dialect study source through
/// <see cref="StudyShaderCompiler"/> and writes its compute-kernel bytecode.</summary>
internal static class StudyCommand {
    public static Command Create() {
        var glslArgument = new Argument<string>(name: "glsl") { Description = "The study source: a Shadertoy-dialect void mainImage(out vec4, in vec2) shader." };
        var nameOption = new Option<string?>(name: "--name") { Description = "The compiled program's name (default: the source file's stem)." };
        var outOption = new Option<string>(name: "--out") { Description = "Directory to write <name>.comp.spv and <name>.comp.dxil into.", Required = true };
        var toolchainOption = new Option<string?>(name: "--toolchain") { Description = "Directory holding glslang (or glslangValidator), spirv-cross, and dxc; omitted resolves each by bare name on the search path." };
        var command = new Command(description: """
            Compile one Shadertoy-dialect study source to both backend bytecodes.

            Wraps the source in Puck's study prelude (the PuckStudy push constants, the iXxx
            globals, PUCK_STUDY's paired camera) and runs it through glslang, spirv-cross, and
            dxc. Every diagnostic prints as <file>:<line>: <message>, with <line> already mapped
            back to the source file.

            Exit codes: 0 compiled, 1 a diagnostic was an error.
            """, name: "study") {
            glslArgument,
            nameOption,
            outOption,
            toolchainOption,
        };

        command.SetAction(action: parseResult => Run(
            glslPath: parseResult.GetRequiredValue(argument: glslArgument),
            name: parseResult.GetValue(option: nameOption),
            outputDirectory: parseResult.GetRequiredValue(option: outOption),
            toolchainDirectory: parseResult.GetValue(option: toolchainOption)
        ));
        return command;
    }

    private static int Run(string glslPath, string? name, string outputDirectory, string? toolchainDirectory) {
        var sourcePath = Path.GetFullPath(path: glslPath);

        if (!File.Exists(path: sourcePath)) {
            Console.Error.WriteLine(value: $"shaders study: source not found: {sourcePath}");

            return 1;
        }

        var resolvedName = (name ?? Path.GetFileNameWithoutExtension(path: sourcePath));
        var resolvedOutputDirectory = Path.GetFullPath(path: outputDirectory);

        Directory.CreateDirectory(path: resolvedOutputDirectory);

        var compiler = new StudyShaderCompiler(cacheDirectory: Path.Combine(path1: resolvedOutputDirectory, path2: ".puck-study-cache"), toolchainDirectory: toolchainDirectory);
        StudyProgram program;

        try {
            program = compiler.Compile(name: resolvedName, sourcePath: sourcePath, sourceText: File.ReadAllText(path: sourcePath));
        } catch (StudyToolMissingException exception) {
            Console.Error.WriteLine(value: $"shaders study: {exception.Message}");

            return 1;
        }

        foreach (var diagnostic in program.Diagnostics) {
            var writer = (diagnostic.IsError ? Console.Error : Console.Out);

            writer.WriteLine(value: $"{sourcePath}:{diagnostic.Line}: {diagnostic.Message}");
        }

        if (program.IsError) {
            return 1;
        }

        var spirvPath = Path.Combine(path1: resolvedOutputDirectory, path2: $"{resolvedName}.comp.spv");
        var dxilPath = Path.Combine(path1: resolvedOutputDirectory, path2: $"{resolvedName}.comp.dxil");

        File.WriteAllBytes(bytes: program.Spirv.ToArray(), path: spirvPath);
        File.WriteAllBytes(bytes: program.Dxil.ToArray(), path: dxilPath);
        Console.Out.WriteLine(value: $"shaders study: wrote {spirvPath} ({program.Spirv.Length} bytes) and {dxilPath} ({program.Dxil.Length} bytes).");

        return 0;
    }
}
