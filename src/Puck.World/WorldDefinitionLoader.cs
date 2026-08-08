namespace Puck.World;

/// <summary>The BOOT world document plus the console's tracked document origin — the DI singleton
/// <c>world.save</c> reads to learn its default target, <c>world.status</c> reads to report its source, and
/// <c>world.reload</c> reads to know what to re-read.</summary>
/// <remarks><see cref="SourcePath"/> is intentionally SETTABLE, not <c>init</c>: a runtime <c>world.load</c> that
/// installs a different document moves what the console considers "the current origin" for every later
/// <c>world.save</c>/<c>world.status</c>/<c>world.reload</c> — see <c>WorldPostBuildWiring.Install</c>'s
/// <see cref="Server.WorldServer.EchoTap"/> subscriber, the ONE writer, which updates it only after the server's own
/// echo confirms the rebuild actually applied (never eagerly at submit time, when the server might still refuse it).
/// <see cref="Definition"/> stays <c>init</c>-only: it is the BOOT document, read once during composition to seed
/// the initial <see cref="Server.WorldServer"/>/population — the running server's OWN live/base definitions are what
/// carry every later state, never this record.</remarks>
/// <param name="Definition">The BOOT world definition (read once, at composition).</param>
/// <param name="SourcePath">The file the current document origin resolves to.</param>
internal sealed record WorldDefinitionSource(WorldDefinition Definition, string SourcePath) {
    /// <summary>The file the current document origin resolves to; see the class remarks for why this is settable and
    /// who the one writer is.</summary>
    public string SourcePath { get; set; } = SourcePath;
}

/// <summary>
/// Resolves the world definition at boot: a <c>--world &lt;path&gt;</c> argument (or the checked-in
/// <c>Assets/worlds/play.world.json</c> beside the executable — the hub, the game's first main city), loaded through
/// <see cref="WorldJsonContext"/>, schema-checked, and passed through <see cref="WorldDefinitionValidator"/>.
/// </summary>
/// <remarks>Every resolved path is an assertion: absent, unreadable, or invalid, it fails the boot with a named reason
/// and a non-zero exit. A typo or missing shipped asset must never quietly run some other world. Boot prints exactly
/// one <c>[world] definition:</c> line naming the successfully loaded file.</remarks>
internal static class WorldDefinitionLoader {
    /// <summary>The default world file, resolved against <see cref="AppContext.BaseDirectory"/> when no
    /// <c>--world</c> path is supplied.</summary>
    public static readonly string DefaultRelativePath = Path.Combine(path1: "Assets", path2: "worlds", path3: "play.world.json");

    /// <summary>Resolves the active world definition from an explicit file or the shipped default file. Failure to
    /// load either file refuses the boot.</summary>
    /// <param name="explicitPath">The <c>--world</c> path, or <see langword="null"/>/empty for the shipped default
    /// file.</param>
    /// <param name="source">The resolved definition and its origin, when this returns <see langword="true"/>.</param>
    /// <param name="failure">The one-line boot-failure message, or empty on success.</param>
    /// <returns><see langword="true"/> when the boot may proceed.</returns>
    public static bool TryResolve(string? explicitPath, out WorldDefinitionSource source, out string failure) {
        var explicitly = !string.IsNullOrWhiteSpace(value: explicitPath);

        string path;

        try {
            path = (explicitly ? Path.GetFullPath(path: explicitPath!) : Path.Combine(path1: AppContext.BaseDirectory, path2: DefaultRelativePath));
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
            source = null!;
            failure = $"[world] definition refused: cannot resolve path '{explicitPath}' ({exception.Message.ReplaceLineEndings(replacementText: " ")})";

            return false;
        }

        if (TryLoadFile(path: path, definition: out var loaded, reason: out var reason)) {
            Console.Error.WriteLine(value: $"[world] definition: {path} ({(explicitly ? "--world" : "shipped default")})");

            source = new WorldDefinitionSource(Definition: loaded!, SourcePath: path);
            failure = string.Empty;

            return true;
        }

        source = null!;
        failure = $"[world] definition refused: {reason}";

        return false;
    }

    /// <summary>Loads and validates a world document from a file — the public seam the runtime <c>world.load</c> verb
    /// reuses so it never reimplements the deserialize → schema-check → validate path. Any failure yields a one-line
    /// reason (line endings collapsed) and <see langword="false"/>, and the three failure classes are named apart:
    /// an ABSENT file, an UNREADABLE file, and an INVALID document. An INCOMPLETE document — one missing a section the
    /// canonical writer emits — is invalid like any other; the validator names every missing section. A broad catch is
    /// deliberate: a load boundary must never throw out of <see cref="TryLoadFile"/>. Delegates to
    /// <see cref="WorldDefinitionFileSource.TryLoad"/> — the one implementation this console path and the replay
    /// tape's offline re-drive (<c>Server.WorldServer.ApplyRebuild</c>, on a REPLAY drive) share, so a live read and a
    /// re-drive's later re-read of the SAME path compute the same content hash — and then resolves every FIRST-FILL
    /// <see cref="WorldDraw"/> site (<see cref="WorldDrawBootResolver"/>) keyed off <paramref name="instanceIdentity"/>,
    /// so a fresh boot and a fresh <c>world.instance.start</c> draw independently while each stays reproducible. The
    /// content hash the inner load computes (and replay CAS-pinning elsewhere compares) is taken over the RAW authored
    /// bytes, BEFORE this resolution step — a draw's outcome never moves the pin.</summary>
    /// <param name="path">The file to load.</param>
    /// <param name="definition">The loaded, draw-resolved definition on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <param name="instanceIdentity">The running instance's own identity — the draw seed ladder's INSTANCE rung.
    /// Defaults to the boot instance's own name.</param>
    /// <returns><see langword="true"/> when the file loaded and validated.</returns>
    public static bool TryLoadFile(string path, out WorldDefinition? definition, out string reason, string instanceIdentity = WorldInstanceHost.BootInstanceName) {
        if (!WorldDefinitionFileSource.TryLoad(path: path, definition: out definition, contentHash: out _, reason: out reason)) {
            return false;
        }

        if (!WorldDrawBootResolver.TryResolve(definition: definition!, instanceIdentity: instanceIdentity, resolved: out var resolved, out reason)) {
            definition = null;
            reason = $"{path} draw refused: {reason}";

            return false;
        }

        // A resolved draw writes a value the validator has already been told the site's domain admits, so this can
        // only fire if a domain narrowing went soft. Loud refusal rather than a silent bad boot.
        if (!WorldDefinitionValidator.TryValidate(definition: resolved, reason: out var resolvedReason)) {
            definition = null;
            reason = $"{path} produced an invalid document after its draws resolved: {resolvedReason}";

            return false;
        }

        definition = resolved;

        return true;
    }
}
