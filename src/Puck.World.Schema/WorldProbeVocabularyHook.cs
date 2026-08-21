namespace Puck.World;

/// <summary>
/// Injection seam for the probes-kind validation that needs the host's REGISTERED vocabulary —
/// <c>Puck.World.Schema</c> must not reference <c>Puck.Shaders</c> (where <c>ProbeKindCatalog</c> lives), while
/// <see cref="WorldDefinitionValidator"/> still must refuse a document naming an unregistered
/// <c>probes[].kind</c> BY NAME, at load. The composition root (<c>Puck.World</c>, <c>Puck.World.Silo</c>,
/// the test suite) wires this with a <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> method,
/// installed before any pre-container document parse the validators run during.
/// </summary>
/// <remarks>
/// REQUIRED, not optional — the same shape as <see cref="WorldExtensionVocabularyHook"/>: the registered kind set is
/// a static directory scan available at module-initializer time, so absence can only mean no composition root
/// installed it. Skipping the check then would validate CLEAN a document naming a kind no host can run.
/// </remarks>
public static class WorldProbeVocabularyHook {
    /// <summary>Answers whether a key names a shipped probe kind (a <c>puck.probe.v1</c> manifest — this project
    /// cannot reference <c>Puck.Shaders</c>, so the catalog never appears here). Installed once by the composition
    /// root's module initializer; read through <see cref="IsRegisteredProbeKind"/>, never directly.</summary>
    public static Func<string, bool>? ProbeKindCheck { get; set; }

    /// <summary>Determines whether <paramref name="kindId"/> names a registered probe kind.</summary>
    /// <param name="kindId">The candidate kind id.</param>
    /// <returns><see langword="true"/> when a kind is registered under that id.</returns>
    /// <exception cref="InvalidOperationException"><see cref="ProbeKindCheck"/> was never installed. The check is
    /// never skipped: skipping it would pass a document no host can run.</exception>
    public static bool IsRegisteredProbeKind(string kindId) {
        return ((ProbeKindCheck is { } check)
            ? check(kindId)
            : throw new InvalidOperationException(message: "WorldProbeVocabularyHook.ProbeKindCheck was never installed — the composition root's module initializer should have wired it before any validator ran; a probe kind key cannot be checked here, and is never assumed valid.")
        );
    }
}
