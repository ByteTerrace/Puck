using System.ComponentModel;
using System.Reflection;
using System.Text;
using Puck.Abstractions;

namespace Puck.Assets;

/// <summary>Shared source snapshots and compiler execution for externally compiled derived products.</summary>
public static class ToolchainBuild {
    /// <summary>The machine setup linked by every missing compiler diagnostic.</summary>
    public const string Setup = "docs/development/contributing.md#setting-up-a-machine";

    /// <summary>Reads an embedded source tree, or its live files when a source directory is supplied.</summary>
    /// <param name="assembly">The assembly carrying the sources.</param>
    /// <param name="prefix">The resource prefix, including its trailing slash.</param>
    /// <param name="directory">A live source root, or null for the built sources.</param>
    /// <returns>Ordinal relative paths and their bytes.</returns>
    public static SortedDictionary<string, byte[]> Sources(Assembly assembly, string prefix, string? directory = null) {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal))) {
            var path = resource[prefix.Length..].Replace('\\', '/');
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            files.Add(path, directory is null ? bytes.ToArray() : CompileInputs.ReadAllBytes(Path.Combine(directory, path)));
        }
        if (files.Count == 0) { throw new InvalidOperationException($"Missing source resources {prefix}."); }
        return files;
    }

    /// <summary>Identifies a source snapshot by names and contents, independent of its host directory.</summary>
    /// <param name="files">The source snapshot.</param>
    /// <returns>The full source content pin.</returns>
    public static string Identity(IReadOnlyDictionary<string, byte[]> files) => ContentPin.Compute(Encoding.UTF8.GetBytes(
        string.Join('\n', files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{ContentPin.Compute(pair.Value)}")))).ToString();

    /// <summary>Runs a noninteractive compiler through the shared bounded child-process boundary.</summary>
    /// <param name="executable">The tool name on PATH, or an explicit executable path.</param>
    /// <param name="arguments">The argument vector.</param>
    /// <param name="directory">The compiler working directory.</param>
    /// <param name="environment">Explicit compiler environment, overriding the neutral build environment.</param>
    /// <returns>The tool's standard output.</returns>
    /// <exception cref="InvalidOperationException">The tool is missing, failed, or exceeded its bound.</exception>
    public static string Run(string executable, IReadOnlyList<string> arguments, string? directory = null, IReadOnlyDictionary<string, string?>? environment = null) {
        try {
            var compilerEnvironment = new Dictionary<string, string?> {
                    ["RUSTFLAGS"] = null, ["CARGO_ENCODED_RUSTFLAGS"] = null, ["CARGO_BUILD_RUSTFLAGS"] = null,
                    ["CARGO_TARGET_WASM32_UNKNOWN_UNKNOWN_RUSTFLAGS"] = null,
                    ["RUSTC_WRAPPER"] = null, ["RUSTC_WORKSPACE_WRAPPER"] = null, ["RUSTC"] = null,
                    ["CARGO_TARGET_DIR"] = null, ["CARGO_BUILD_TARGET_DIR"] = null,
                    ["RUSTC_BOOTSTRAP"] = null, ["CPATH"] = null, ["C_INCLUDE_PATH"] = null,
                    ["CPLUS_INCLUDE_PATH"] = null, ["LIBRARY_PATH"] = null,
                };
            if (environment is not null) {
                foreach (var (name, value) in environment) { compilerEnvironment[name] = value; }
            }
            var run = ChildProcess.RunAsync(ChildProcess.FindExecutable(executable), arguments, workingDirectory: directory,
                input: string.Empty, timeout: TimeSpan.FromMinutes(2), environment: compilerEnvironment).GetAwaiter().GetResult();
            if (run.TimedOut || run.ExitCode != 0) {
                throw new InvalidOperationException($"{executable} {(run.TimedOut ? "timed out" : $"exited {run.ExitCode}")}: {run.Stderr.Trim()} See {Setup}.");
            }
            return run.Stdout.Trim();
        } catch (Exception exception) when (exception is Win32Exception or FileNotFoundException) {
            throw new InvalidOperationException($"Required toolchain '{executable}' is unavailable. See {Setup}.", exception);
        }
    }

    /// <summary>Checks a compiler's version and returns its complete reported identity.</summary>
    /// <param name="executable">The compiler executable.</param>
    /// <param name="minimum">The minimum admitted version.</param>
    /// <param name="arguments">Version query arguments.</param>
    /// <returns>The tool's version identity.</returns>
    public static string Identify(string executable, Version minimum, params string[] arguments) {
        var identity = Run(executable, arguments.Length == 0 ? ["--version"] : arguments);
        var version = identity.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => Version.TryParse(word, out var parsed) ? parsed : null).FirstOrDefault(value => value is not null);
        if (version is null || version < minimum) {
            throw new InvalidOperationException($"Required toolchain '{executable}' needs version {minimum} or later; got {identity}. See {Setup}.");
        }
        var executablePath = ChildProcess.FindExecutable(executable);
        return identity.Replace('\\', '/') + "\n" + ContentPin.Compute(File.ReadAllBytes(executablePath));
    }

    /// <summary>Materializes a source snapshot in a run directory and builds one product.</summary>
    /// <param name="files">The immutable source snapshot.</param>
    /// <param name="build">The build over the materialized source directory.</param>
    /// <returns>The built product.</returns>
    public static byte[] Derive(IReadOnlyDictionary<string, byte[]> files, Func<string, byte[]> build) {
        using var run = RunDirectory.Create("puck-toolchain-");
        foreach (var (name, bytes) in files) {
            var path = Path.GetFullPath(Path.Combine(run.Path, name));
            if (!path.StartsWith(run.Path + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                throw new InvalidDataException($"Source path escapes compiler workspace: {name}.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        var product = build(run.Path);
        run.Conclude(true);
        return product;
    }
}
