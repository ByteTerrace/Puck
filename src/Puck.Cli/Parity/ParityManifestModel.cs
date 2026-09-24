namespace Puck.Cli.Parity;

/// <summary>One decoded <c>puck.parity.manifest.v1</c> document — the pinned manifest a parity capture
/// pipeline writes beside its frames, and this comparator reads. <see cref="Captures"/> preserves document
/// order.</summary>
internal sealed record ParityManifest(
    string Backend,
    string World,
    IReadOnlyList<ParityManifestCapture> Captures
);
/// <summary>One armed capture's entry. When <see cref="Refusal"/> is present the producer wrote no frame for it —
/// <c>cameraInside</c>, <c>busy</c>, <c>stale</c>, <c>failed</c> or <c>unserved</c>, with <see cref="Detail"/>
/// naming the ticks involved — so <see cref="Frame"/> and <see cref="Census"/> are <see langword="null"/>, and only
/// <see cref="StateHash"/>, the sim-state summary at the armed tick, is meaningful.</summary>
internal sealed record ParityManifestCapture(
    string Station,
    ulong Tick,
    string StateHash,
    string? Refusal,
    string? Detail,
    string? Frame,
    IReadOnlyDictionary<string, long>? Census
);
