using System.Diagnostics;
using System.Text;

namespace Puck.Shaders;

/// <summary>Resolves shader tools from a caller-selected directory or the process search path.</summary>
public sealed class ShaderToolchain {
    public ShaderToolchain(string? directory = null) {
        Directory = (string.IsNullOrWhiteSpace(value: directory)
            ? null
            : Path.GetFullPath(path: directory)
        );
    }

    public string? Directory { get; }
    /// <summary>Stable identity included in cache keys, including executable version metadata.</summary>
    public string Identity {
        get {
            var builder = new StringBuilder(value: (Directory ?? "PATH"));
            var path = (Find(name: ShaderCompiler.DxcTool) ?? (ResolvePath(name: ShaderCompiler.DxcTool) ?? ShaderCompiler.DxcTool));

            builder.Append(value: '|').Append(value: path);
            if (File.Exists(path: path)) {
                builder.Append(value: '|').Append(value: File.GetLastWriteTimeUtc(path: path).Ticks);
                try {
                    var info = FileVersionInfo.GetVersionInfo(fileName: path);

                    builder.Append(value: '|').Append(value: info.FileVersion);
                } catch (Exception) { }
            }
            return ShaderSourceClosure.HashOf(text: builder.ToString());
        }
    }

    private string? Find(string name) {
        if (Directory is null) { return null; }
        var path = Path.Combine(
            path1: Directory,
            path2: name
        );

        if (File.Exists(path: path)) { return path; }
        path += ".exe";
        return (File.Exists(path: path)
            ? path
            : null
        );
    }
    private static string? ResolvePath(string name) {
        var pathVariable = Environment.GetEnvironmentVariable(variable: "PATH");

        if (string.IsNullOrWhiteSpace(value: pathVariable)) { return null; }
        foreach (var directory in pathVariable.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: Path.PathSeparator
        )) {
            var candidate = Path.Combine(
                path1: directory,
                path2: name
            );

            if (File.Exists(path: candidate)) { return candidate; }
            if (File.Exists(path: (candidate + ".exe"))) { return (candidate + ".exe"); }
        }
        return null;
    }

    /// <summary>Finds a tool's full path: in <see cref="Directory"/> when one is set, otherwise on the process search
    /// path.</summary>
    /// <param name="name">The tool's file name, with or without the <c>.exe</c> extension.</param>
    /// <returns>The tool's full path, or <see langword="null"/> when it is not found.</returns>
    public string? Locate(string name) =>
        ((Directory is null)
            ? ResolvePath(name: name)
            : Find(name: name)
        );
    /// <summary>Returns the executable to run for a tool: its bare name, resolved by the operating system on the search
    /// path, when no <see cref="Directory"/> is set, otherwise its full path inside the directory.</summary>
    /// <param name="name">The tool's file name, with or without the <c>.exe</c> extension.</param>
    /// <returns>The name or full path to run.</returns>
    /// <exception cref="ShaderToolMissingException">The tool is not in <see cref="Directory"/>.</exception>
    public string Resolve(string name) {
        if (Directory is null) { return name; }
        return (Find(name: name) ?? throw new ShaderToolMissingException(
            name,
            Directory
        ));
    }
}
