using System.Security.Cryptography;
using System.Text;

namespace Puck.Shaders;

/// <summary>Resolves shader tools from a caller-selected directory, a caller-selected <c>dxc</c> executable, or the
/// process search path.</summary>
public sealed class ShaderToolchain {
    private readonly string? m_dxc;

    /// <summary>Initializes a toolchain that finds its tools in <paramref name="directory"/>, or on the process search
    /// path when it is <see langword="null"/> or empty.</summary>
    /// <param name="directory">The directory holding the tools, or <see langword="null"/>.</param>
    public ShaderToolchain(string? directory = null)
        : this(
        directory: (string.IsNullOrWhiteSpace(value: directory) ? null : Path.GetFullPath(path: directory)),
        dxc: null
    ) { }

    // A named executable resolves on PATH unless the command itself specifies a directory.
    private ShaderToolchain(string? directory, string? dxc) {
        m_dxc = (((dxc is null) || (dxc.IndexOfAny(anyOf: ['/', '\\']) < 0)) ? dxc : Path.GetFullPath(path: dxc));
        Directory = (((m_dxc is null) || !Path.IsPathRooted(path: m_dxc)) ? directory : Path.GetDirectoryName(path: m_dxc));
    }

    /// <summary>Returns the toolchain a build's <c>DxcCommand</c> names: a bare tool name (<c>dxc</c>) resolves on the
    /// process search path, preserving the requested name, and anything holding a directory separator is the
    /// <c>dxc</c> executable itself, whatever its file name.</summary>
    /// <param name="command">The command, such as <c>dxc</c> or <c>/opt/dxc/bin/dxc</c>.</param>
    /// <returns>The toolchain.</returns>
    public static ShaderToolchain OfCommand(string? command) {
        if (
            string.IsNullOrWhiteSpace(value: command) ||
            string.Equals(a: command, b: ShaderCompiler.DxcTool, comparisonType: StringComparison.Ordinal)
        ) {
            return new ShaderToolchain();
        }

        return new ShaderToolchain(directory: null, dxc: command);
    }

    /// <summary>Gets the directory the tools resolve in, or <see langword="null"/> for the process search path.</summary>
    public string? Directory { get; }
    /// <summary>Gets the identity every cache key includes: the content of the <c>dxc</c> executable and of the compiler
    /// and validator libraries found in the standard toolchain locations (<c>dxcompiler</c> and <c>dxil</c>), each by its file name and SHA-256, read
    /// without running the tool. Nothing about where the toolchain is installed or when its files were written enters
    /// it, so a cache restored onto another machine or extracted afresh is a hit for the same toolchain. A <c>dxc</c>
    /// that cannot be found has an identity of its own, under which nothing is ever compiled.</summary>
    /// <remarks>The library search covers colocated releases and Windows system/PATH locations. Native-loader
    /// overrides such as <c>LD_LIBRARY_PATH</c> and <c>DXC_DXIL_DLL_PATH</c> are not resolved here.</remarks>
    /// <exception cref="IOException">A toolchain file exists but cannot be read; the message names it.</exception>
    public string Identity {
        get {
            // The dxc a compile runs (Resolve): the named executable, the one in Directory, or the one on the search path.
            var dxc = Locate(name: ShaderCompiler.DxcTool);

            if ((dxc is null) || !File.Exists(path: dxc)) {
                return ShaderSourceClosure.HashOf(text: "dxc|absent");
            }

            var builder = new StringBuilder();

            foreach (var file in FilesOf(dxc: dxc)) {
                builder.Append(value: Path.GetFileName(path: file).ToLowerInvariant()).Append(value: '|').Append(value: HashOf(path: file)).Append(value: '\n');
            }

            return ShaderSourceClosure.HashOf(text: builder.ToString());
        }
    }

    // Standard release locations: the executable, then compiler and validator libraries beside it or in the sibling
    // lib directory, plus the Windows system directory and PATH. Other native-loader search rules are not modeled.
    private static IEnumerable<string> FilesOf(string dxc) {
        yield return dxc;

        var directory = (Path.GetDirectoryName(path: Path.GetFullPath(path: dxc)) ?? string.Empty);
        string[] libraries = (OperatingSystem.IsWindows()
            ? ["dxcompiler.dll", "dxil.dll"]
            : (OperatingSystem.IsMacOS() ? ["libdxcompiler.dylib", "libdxil.dylib"] : ["libdxcompiler.so", "libdxil.so"]));

        foreach (var library in libraries) {
            var found = LibraryCandidates(directory: directory).Select(selector: candidate => Path.Combine(path1: candidate, path2: library)).FirstOrDefault(predicate: File.Exists);

            if (found is not null) {
                yield return found;
            }
        }
    }
    private static IEnumerable<string> LibraryCandidates(string directory) {
        yield return directory;
        if (!OperatingSystem.IsWindows()) {
            yield return Path.Combine(path1: directory, path2: "..", path3: "lib");
            yield break;
        }
        yield return Environment.SystemDirectory;
        foreach (var entry in (Environment.GetEnvironmentVariable(variable: "PATH") ?? string.Empty).Split(options: StringSplitOptions.RemoveEmptyEntries, separator: Path.PathSeparator)) {
            yield return entry;
        }
    }
    // Read the bytes again: replacing a tool or library can preserve both its length and its write time.
    private static string HashOf(string path) {
        try {
            using var stream = new FileStream(access: FileAccess.Read, bufferSize: 81920, mode: FileMode.Open, options: FileOptions.SequentialScan, path: path, share: FileShare.Read | FileShare.Delete);

            return Convert.ToHexStringLower(inArray: SHA256.HashData(source: stream));
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            throw new IOException(message: $"The shader toolchain file '{path}' cannot be read: {exception.Message}", innerException: exception);
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

            if (File.Exists(path: candidate)) { return Path.GetFullPath(path: candidate); }
            if (File.Exists(path: (candidate + ".exe"))) { return Path.GetFullPath(path: (candidate + ".exe")); }
        }
        return null;
    }
    private bool IsDxc(string name) =>
        ((m_dxc is not null) && (string.Equals(a: name, b: ShaderCompiler.DxcTool, comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: name, b: (ShaderCompiler.DxcTool + ".exe"), comparisonType: StringComparison.OrdinalIgnoreCase)));

    /// <summary>Finds a tool's full path: the named <c>dxc</c> executable when the toolchain names one, in
    /// <see cref="Directory"/> when one is set, otherwise on the process search path.</summary>
    /// <param name="name">The tool's file name, with or without the <c>.exe</c> extension.</param>
    /// <returns>The tool's full path, or <see langword="null"/> when it is not found.</returns>
    public string? Locate(string name) {
        if (IsDxc(name: name)) {
            return ((Directory is null) ? ResolvePath(name: m_dxc!) : (File.Exists(path: m_dxc) ? m_dxc : null));
        }

        return ((Directory is null)
            ? ResolvePath(name: name)
            : Find(name: name)
        );
    }
    /// <summary>Returns the executable to run for a tool: the named <c>dxc</c> executable when the toolchain names one,
    /// its full path on the search path, otherwise its full path inside the directory. An absent tool with no
    /// <see cref="Directory"/> retains its requested name so the process runner reports the missing tool.</summary>
    /// <param name="name">The tool's file name, with or without the <c>.exe</c> extension.</param>
    /// <returns>The name or full path to run.</returns>
    /// <exception cref="ShaderToolMissingException">The tool is not in <see cref="Directory"/>.</exception>
    public string Resolve(string name) {
        var found = Locate(name: name);

        if (found is not null) { return found; }
        if (Directory is null) { return (IsDxc(name: name) ? m_dxc! : name); }
        throw new ShaderToolMissingException(
            name,
            Directory
        );
    }
}
