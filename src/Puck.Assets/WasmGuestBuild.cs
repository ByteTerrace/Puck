using System.Text.Json;


namespace Puck.Assets;

/// <summary>An authored Rust guest variant. Its output paths are logical names, never authoritative built bytes.</summary>
/// <param name="Crate">Cargo package name.</param>
/// <param name="Features">Explicit features, or null for the crate defaults.</param>
/// <param name="Outputs">Logical module paths relative to the package root.</param>
public sealed record WasmGuestBuild(string Crate, IReadOnlyList<string>? Features, IReadOnlyList<string> Outputs) {
    private static readonly Lazy<DerivedBytes> Cache = new(() => new("wasm-guest",
        [("Puck.Assets", "Puck.Assets.WasmGuestBuild", "Build")]));

    /// <summary>The complete Cargo argument vector for this variant.</summary>
    public IReadOnlyList<string> CargoArguments => ["build", "--locked", "--offline", "--release", "--target", "wasm32-unknown-unknown", "--package", Crate,
        .. (Features is { } features ? (IEnumerable<string>)["--no-default-features", "--features", string.Join(',', features)] : [])];

    /// <summary>Cargo's output path relative to the workspace.</summary>
    public string Module => $"target/wasm32-unknown-unknown/release/{Crate.Replace('-', '_')}.wasm";

    /// <summary>Reads the authored variant inventory, also available without Cargo in shipped builds.</summary>
    /// <param name="workspace">Live Rust workspace, or null for the sources embedded in this engine.</param>
    /// <returns>The declared guest builds.</returns>
    public static IReadOnlyList<WasmGuestBuild> ReadBuilds(string? workspace = null) {
        var files = ReadSources(workspace);
        using var document = JsonDocument.Parse(files["builds.json"]);
        return document.RootElement.EnumerateArray().Select(entry => new WasmGuestBuild(
            entry.GetProperty("crate").GetString()!,
            entry.GetProperty("features").ValueKind == JsonValueKind.Null ? null : Strings(entry.GetProperty("features")),
            Strings(entry.GetProperty("outputs")))).OrderBy(build => build.Outputs[0], StringComparer.Ordinal).ToArray();
    }

    private static string[] Strings(JsonElement element) => element.EnumerateArray().Select(item => item.GetString()!).ToArray();
    private static SortedDictionary<string, byte[]> ReadSources(string? workspace) =>
        ToolchainBuild.Sources(typeof(WasmGuestBuild).Assembly, "Puck.WasmSources/", workspace);

    /// <summary>Derives this variant through the shared counted cache.</summary>
    /// <param name="workspace">Live sources, or null for built-in sources.</param>
    /// <param name="cacheDirectory">Explicit local cache, or the per-user cache.</param>
    /// <param name="packageDirectory">Shipping cache to read or prewarm.</param>
    /// <param name="prewarm">Whether to populate the shipping cache.</param>
    /// <returns>The compiled guest bytes.</returns>
    public byte[] GetImage(string? workspace = null, string? cacheDirectory = null, string? packageDirectory = null, bool prewarm = false) {
        var files = ReadSources(workspace);
        var inputs = $"{Crate} {string.Join(' ', CargoArguments)} {ToolchainBuild.Identity(files)}";
        return Cache.Value.GetToolchain(inputs,
            () => ToolchainBuild.Identify("cargo", new Version(1, 96, 0)) + "\n" + ToolchainBuild.Identify("rustc", new Version(1, 96, 0), "-vV"),
            () => ToolchainBuild.Derive(files, Build), cacheDirectory, packageDirectory, prewarm);
    }

    private byte[] Build(string directory) {
        ToolchainBuild.Run("cargo", CargoArguments, directory, new Dictionary<string, string?> {
            ["CARGO_ENCODED_RUSTFLAGS"] = $"--remap-path-prefix={directory}=.",
            ["CARGO_HOME"] = Path.Combine(directory, ".cargo-home"),
        });
        return File.ReadAllBytes(Path.Combine(directory, Module));
    }

    /// <summary>Resolves a declared logical module through its sources even if an old dist file exists.</summary>
    /// <param name="path">Absolute requested module path.</param>
    /// <param name="bytes">Derived bytes when the module is declared.</param>
    /// <returns>Whether this is a source-backed module.</returns>
    public static bool TryRead(string path, out byte[] bytes) {
        if (TryFind(path, out var build, out var workspace)) {
            bytes = build!.GetImage(workspace);
            return true;
        }
        bytes = [];
        return false;
    }

    /// <summary>Recognizes a declared source-backed module without compiling it.</summary>
    /// <param name="path">Requested absolute module path.</param>
    /// <param name="build">The declared variant.</param>
    /// <param name="workspace">Live source workspace, or null for packaged sources.</param>
    /// <returns>Whether the logical module exists in the source inventory.</returns>
    public static bool TryFind(string path, out WasmGuestBuild? build, out string? workspace) {
        build = null;
        workspace = null;
        var normalized = path.Replace('\\', '/');
        if (!normalized.Contains("/wasm/", StringComparison.Ordinal) || !normalized.EndsWith(".wasm", StringComparison.Ordinal)) {
            return false;
        }
        var liveWorkspace = normalized[..normalized.LastIndexOf("/wasm/", StringComparison.Ordinal)] + "/wasm";
        workspace = Directory.Exists(liveWorkspace) ? liveWorkspace : null;
        foreach (var candidate in ReadBuilds(workspace)) {
            var output = candidate.Outputs.FirstOrDefault(output => normalized.EndsWith("/" + output, StringComparison.Ordinal));
            if (output is null) { continue; }
            var root = normalized[..^output.Length];
            var live = Path.Combine(root, "wasm");
            workspace = Directory.Exists(live) ? live : null;
            build = candidate;
            return true;
        }
        return false;
    }
}
