namespace Puck.Cli.Parity;

/// <summary>One decoded <c>puck.parity.manifest.v1</c> document — the pinned manifest a parity capture
/// pipeline writes beside its frames, and this comparator reads. <see cref="Captures"/> preserves document
/// order.</summary>
internal sealed record ParityManifest(
    string Backend,
    string World,
    IReadOnlyList<ParityManifestCapture> Captures
);
/// <summary>One scheduled capture entry. When <see cref="CameraInside"/> is <see langword="true"/> the capture
/// was refused (<c>map(cameraPos) &lt;= 0</c>): <see cref="Frame"/> and <see cref="Census"/> are
/// <see langword="null"/>, and only <see cref="StateHash"/> — the sim-state summary, independent of whether
/// anything was rendered — is meaningful.</summary>
internal sealed record ParityManifestCapture(
    string Station,
    ulong Tick,
    string StateHash,
    bool CameraInside,
    string? Frame,
    IReadOnlyDictionary<string, long>? Census
);
