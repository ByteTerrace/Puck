using System.Diagnostics;
using System.Security.Cryptography;
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
            var names = new[] { "dxc", "glslang", "glslangValidator", "spirv-cross" };
            var builder = new StringBuilder(value: (Directory ?? "PATH"));

            foreach (var name in names) {
                var path = (Find(name: name) ?? (ResolvePath(name: name) ?? name));

                builder.Append(value: '|').Append(value: path);
                if (File.Exists(path: path)) {
                    builder.Append(value: '|').Append(value: File.GetLastWriteTimeUtc(path: path).Ticks);
                    try {
                        var info = FileVersionInfo.GetVersionInfo(fileName: path);

                        builder.Append(value: '|').Append(value: info.FileVersion);
                    } catch (Exception) { }
                }
            }
            return Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: builder.ToString())));
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

    public string Resolve(string name, string? fallbackName = null) {
        if (Directory is null) { return name; }
        return (Find(name: name) ?? (((fallbackName is not null)
            ? Find(name: fallbackName)
            : null)
            ?? throw new ShaderToolMissingException(
            ((fallbackName is null)
            ? name
            : $"{name} (or {fallbackName})"),
            Directory
        )));
    }
}
