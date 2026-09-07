using System.Reflection;

namespace Puck.World.Browser.Engine;

/// <summary>The engine identity every export answers with — pure C#, unaware of the JS interop shims in
/// <c>Puck.World.Browser.Exports</c> so it can be linked directly into
/// <c>tests/Puck.World.Browser.Tests</c> as source.</summary>
public static class BrowserEngineInfo {
    /// <summary>Gets the informational version's SourceLink commit suffix (the same convention the schema's own
    /// <c>x-puck.commit</c> reads), or <see langword="null"/> when the build carries none.</summary>
    private static string? Commit { get; } = ReadCommit();

    private static string? ReadCommit() {
        var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(value: informational)) {
            return null;
        }

        var plusIndex = informational.IndexOf(value: '+');

        return ((plusIndex >= 0) ? informational[(plusIndex + 1)..] : null);
    }

    /// <summary>Builds the version payload every <c>Version</c> export returns.</summary>
    public static BrowserVersion Describe() => new(
        SchemaVersion: WorldDefinition.SchemaVersion,
        Engine: "Puck.World.Browser",
        Commit: Commit
    );
}

/// <summary>The engine identity payload — the JSON-string wire shape every <c>Version</c> export marshals.</summary>
/// <param name="SchemaVersion">The world document schema tag this build parses and validates against.</param>
/// <param name="Engine">The engine's own name, so a caller juggling more than one embedded engine can tell them apart.</param>
/// <param name="Commit">The build's SourceLink commit suffix, or <see langword="null"/> when the build carries none
/// (a local, non-CI build).</param>
public readonly record struct BrowserVersion(string SchemaVersion, string Engine, string? Commit);
