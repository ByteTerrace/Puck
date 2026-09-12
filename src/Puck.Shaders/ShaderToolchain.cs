using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Puck.Shaders;

/// <summary>Resolves shader tools from a caller-selected directory or the process search path.</summary>
public sealed class ShaderToolchain
{
    public ShaderToolchain(string? directory = null)
    {
        Directory = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
    }

    public string? Directory { get; }

    public string Resolve(string name, string? fallbackName = null)
    {
        if (Directory is null) { return name; }
        return Find(name) ?? (fallbackName is not null ? Find(fallbackName) : null)
            ?? throw new ShaderToolMissingException(fallbackName is null ? name : $"{name} (or {fallbackName})", Directory);
    }

    /// <summary>Stable identity included in cache keys, including executable version metadata.</summary>
    public string Identity
    {
        get
        {
            var names = new[] { "dxc", "glslang", "glslangValidator", "spirv-cross" };
            var builder = new StringBuilder(Directory ?? "PATH");
            foreach (var name in names)
            {
                var path = Find(name) ?? ResolvePath(name) ?? name;
                builder.Append('|').Append(path);
                if (File.Exists(path))
                {
                    builder.Append('|').Append(File.GetLastWriteTimeUtc(path).Ticks);
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(path);
                        builder.Append('|').Append(info.FileVersion);
                    }
                    catch (Exception) { }
                }
            }
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
    }

    private static string? ResolvePath(string name) {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable)) { return null; }
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) { return candidate; }
            if (File.Exists(candidate + ".exe")) { return candidate + ".exe"; }
        }
        return null;
    }

    private string? Find(string name)
    {
        if (Directory is null) { return null; }
        var path = Path.Combine(Directory, name);
        if (File.Exists(path)) { return path; }
        path += ".exe";
        return File.Exists(path) ? path : null;
    }
}
