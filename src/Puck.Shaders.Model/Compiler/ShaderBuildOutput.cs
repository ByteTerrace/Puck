using System.Globalization;

namespace Puck.Shaders;

/// <summary>One bytecode file a project's build writes beside its stage source: <c>build/Shaders.targets</c> declares
/// one for every stage source and enabled target, and the build host hands them to the shader build one a line.</summary>
/// <param name="Stage">The source's stage.</param>
/// <param name="Target">The output's target.</param>
/// <param name="SourcePath">The stage source's full path.</param>
/// <param name="OutputPath">The bytecode file's full path.</param>
public sealed record ShaderBuildOutput(ShaderStage Stage, ShaderTarget Target, string SourcePath, string OutputPath) {
    /// <summary>Reads one request line: the stage, the target, the source and the output, separated by tabs.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The output.</returns>
    /// <exception cref="FormatException">The line is not four tab-separated fields naming a stage and a target.</exception>
    public static ShaderBuildOutput Parse(string line) {
        ArgumentNullException.ThrowIfNull(argument: line);

        var fields = line.Split(separator: '\t');

        if (
            (fields.Length != 4) ||
            !Enum.TryParse<ShaderStage>(ignoreCase: false, result: out var stage, value: fields[0]) ||
            !Enum.IsDefined(value: stage) ||
            !Enum.TryParse<ShaderTarget>(ignoreCase: false, result: out var target, value: fields[1]) ||
            !Enum.IsDefined(value: target) ||
            (fields[2].Length == 0) ||
            (fields[3].Length == 0)
        ) {
            throw new FormatException(message: string.Create(provider: CultureInfo.InvariantCulture, handler: $"'{line}' is not a shader build request line: stage, target, source and output, separated by tabs."));
        }

        return new ShaderBuildOutput(
            OutputPath: Path.GetFullPath(path: fields[3]),
            SourcePath: Path.GetFullPath(path: fields[2]),
            Stage: stage,
            Target: target
        );
    }
    /// <summary>Writes this output as one request line, which <see cref="Parse"/> reads back.</summary>
    /// <returns>The line.</returns>
    public string Format() => $"{Stage}\t{Target}\t{SourcePath}\t{OutputPath}";
}
