using System.Security.Cryptography;

namespace Puck.World;

/// <summary>The credential created by a host for one activation. Tape and journal controls stay with the server.</summary>
public sealed class WorldPrivateDrawKeys : WorldDrawSampler {
    private WorldPrivateDrawKeys(byte[] seed, bool recordedOnly = false, bool canInstall = true, bool preview = false)
        : base(seed, recordedOnly, canInstall, preview) { }

    /// <summary>Creates an independent stream family before the activation begins.</summary>
    public static WorldPrivateDrawKeys Create() => new(RandomNumberGenerator.GetBytes(32));
    /// <summary>A deterministic sampler for an isolated authoring run.</summary>
    public new static WorldPrivateDrawKeys Preview => new(new byte[32], preview: true);
    /// <summary>The authority for a reproducible scenario run. Its draws are live state within that run.</summary>
    public static WorldPrivateDrawKeys ForScenario() => new(new byte[32]);
    internal new static WorldPrivateDrawKeys Missing => new(new byte[32], canInstall: false);
    internal static WorldPrivateDrawKeys Recorded => new(new byte[32], recordedOnly: true);
    internal new void AdvanceGeneration() => base.AdvanceGeneration();
    internal new void TrackRewind(ArenaJournal journal) => base.TrackRewind(journal);
    internal new IDisposable Capture(Action<PrivateDrawWord> observe) => base.Capture(observe);
    internal new IDisposable Replay(IEnumerable<PrivateDrawWord> words, Func<string, Exception?> missing, bool historical = false, IReadOnlySet<long>? sites = null) =>
        base.Replay(words, missing, historical, sites);
    internal new WorldDefinition Install(WorldDefinition definition, WorldDefinition? previous = null, WorldDrawPositions? restore = null) =>
        base.Install(definition, previous, restore);
    internal WorldStateRow InstallRow(WorldStateRow row, WorldStateRow? previous, ref long last) =>
        base.InstallRow(row, previous, ref last);
}
