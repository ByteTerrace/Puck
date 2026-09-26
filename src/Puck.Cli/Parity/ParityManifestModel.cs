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
/// <c>cameraInside</c>, <c>busy</c>, <c>stale</c>, <c>failed</c>, <c>unserved</c> or <c>deviceLost</c>, with <see cref="Detail"/>
/// naming the ticks involved — so <see cref="Frame"/>, <see cref="Census"/> and <see cref="RegionTick"/> are
/// <see langword="null"/>, and only <see cref="StateHash"/>, the sim-state summary at the armed tick, is meaningful. A
/// landed frame carries <see cref="RegionTick"/>, the tick the frame refreshed its bound regions at, and a landed capture
/// of a source instance whose source states its image carries <see cref="SourceVerdict"/>, its exact verdict against that
/// image.</summary>
internal sealed record ParityManifestCapture(
    string Station,
    ulong Tick,
    string StateHash,
    ulong? RegionTick,
    string? Refusal,
    string? Detail,
    string? Frame,
    IReadOnlyDictionary<string, long>? Census,
    ParityManifestSourceVerdict? SourceVerdict = null
);
/// <summary>A landed capture's exact verdict against the image its source states it shows.</summary>
/// <param name="Holds">Whether every pixel matched the source's reference of the captured tick.</param>
/// <param name="Detail">The verdict's prose.</param>
internal sealed record ParityManifestSourceVerdict(bool Holds, string Detail);
