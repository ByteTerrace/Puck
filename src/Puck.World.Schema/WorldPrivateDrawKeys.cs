using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The installation ordinals of a document's private streams. These addresses carry no keys.</summary>
public sealed record WorldDrawPositions([property: JsonRequired] long Last, [property: JsonRequired] Dictionary<string, long> Sites) {
    [JsonIgnore]
    public bool IsValid => Last >= 0 && Sites is not null && Sites.All(p => !string.IsNullOrEmpty(p.Key) && p.Value > 0 && p.Value <= Last)
        && Sites.Values.Distinct().Count() == Sites.Count;
}

/// <summary>The local authority portion of a save. Row content and cursors live in the document.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldDrawSaveState([property: JsonRequired] byte[] Seed, [property: JsonRequired] long Generation, [property: JsonRequired] WorldDrawPositions Positions);

/// <summary>One world's sampler. Only authority persistence receives its seed. Replays consume recorded words.</summary>
public sealed class WorldPrivateDrawKeys : PrivateDrawAuthority {
    private readonly bool m_authority;
    private readonly Lazy<WorldDrawSaveState?>? m_localSave;
    private string? m_identity;
    private byte[]? m_localKey;
    private string? LocalIdentity { get; init; }
    private ReadOnlySpan<byte> StorageSeed => m_localSave?.Value?.Seed is { } saved ? saved : base.Seed;
    protected override ReadOnlySpan<byte> Seed => LocalIdentity is null ? StorageSeed :
        m_localKey ??= HMACSHA256.HashData(StorageSeed, System.Text.Encoding.UTF8.GetBytes(LocalIdentity));
    public override long RootGeneration => m_localSave?.Value?.Generation ?? base.RootGeneration;
    private readonly bool m_missing;
    protected override bool RecordedOnly { get; }
    /// <summary>A sampler that can only read recorded words.</summary>
    public static WorldPrivateDrawKeys Recorded => new(new byte[32], false, recordedOnly: true);
    /// <summary>Opens a private save only when the first private declaration needs it.</summary>
    public static WorldPrivateDrawKeys Local(string identity, Func<WorldDrawSaveState?> resume) =>
        new(RandomNumberGenerator.GetBytes(32), true, localSave: new(resume)) { LocalIdentity = identity };
    protected override bool CanInstall {
        get { _ = m_localSave?.Value; return !m_missing; }
    }
    /// <summary>A host without authority credentials refuses its first private declaration.</summary>
    public static WorldPrivateDrawKeys Missing => new(new byte[32], false, missing: true);

    private WorldPrivateDrawKeys(byte[] seed, bool authority, long generation = 0, bool missing = false, long rootGeneration = 0, bool recordedOnly = false, Lazy<WorldDrawSaveState?>? localSave = null) : base(seed, generation, rootGeneration) {
        m_localSave = localSave;
        m_authority = authority;
        m_missing = missing;
        RecordedOnly = recordedOnly;
    }

    /// <summary>Nonsecret reference used to check a checkpoint against the authority restoring it.</summary>
    public string? Identity => m_authority ? m_identity ??= Puck.Assets.ContentPin.Compute(Seed).Hex : null;

    /// <summary>A fresh authority lifetime.</summary>
    public static WorldPrivateDrawKeys Create() => new(RandomNumberGenerator.GetBytes(32), true);

    /// <summary>A deterministic isolated sampler for proofs and tests.</summary>
    public static WorldPrivateDrawKeys Preview => new(new byte[32], false);

    /// <summary>Restores the seed carried by this world's authority state.</summary>
    public static WorldPrivateDrawKeys FromAuthorityState(byte[] seed, long generation = 0, long rootGeneration = 0) =>
        seed is { Length: 32 } && seed.AsSpan().IndexOfAnyExcept((byte)0) >= 0
            ? new(seed, true, generation, rootGeneration: rootGeneration) : throw new InvalidDataException("Invalid private draw authority state.");

    internal WorldDefinition InstallAtBoot(WorldDefinition definition) {
        if (!definition.AuthoredState.Any(row => row.Draw?.PrivateStream == true)) { return definition; }
        var saved = m_localSave?.Value;
        if (m_localSave is not null && saved is null && definition.AuthoredState.Any(row => row.Draw?.PrivateStream == true && row.DrawCursor > 0)) {
            throw new InvalidDataException("PrivateDrawSeedMissing: document carries private draw positions without its authority save.");
        }
        return Install(definition, restore: saved?.Positions, authoredSave: true);
    }

    /// <summary>Returns a copy for the authority's private store.</summary>
    public byte[]? SerializeAuthorityState() => m_authority ? StorageSeed.ToArray() : null;

    /// <summary>Records the nonsecret stream addresses beside an authority or replay document.</summary>
    public static WorldDrawPositions? Positions(WorldDefinition definition) {
        if (definition.StateRaw is not { DrawInstallCount: > 0 } state) { return null; }
        return new(state.DrawInstallCount, definition.AuthoredState.Where(r => r.Draw?.Secret is not null)
            .ToDictionary(r => r.Name.Value, r => r.Draw!.Secret!.Installation, StringComparer.Ordinal));
    }

    /// <summary>Binds declarations to the world's streams. A writer can supply neither an installation ordinal
    /// nor a position in a retained stream. Restore takes its ordinals from the authority continuation.</summary>
    public WorldDefinition Install(WorldDefinition definition, WorldDefinition? previous = null, WorldDrawPositions? restore = null, bool authoredSave = false) {
        var last = restore?.Last ?? previous?.StateRaw?.DrawInstallCount ?? 0L;
        List<WorldStateRow>? rows = null;
        for (var index = 0; index < definition.AuthoredState.Count; index++) {
            var row = definition.AuthoredState[index];
            if (row.Draw?.PrivateStream != true) { continue; }
            long? ordinal = null;
            if (restore is not null) {
                if (restore.Sites.TryGetValue(row.Name.Value, out var id) && id > 0 && id <= last) {
                    ordinal = id;
                } else if (!authoredSave) {
                    throw new InvalidDataException("Invalid private draw installation in the authority continuation.");
                }
            }
            var old = previous is null ? null : WorldDefinitionRows.FindStateRow(previous.AuthoredState, row.Name.Value);
            var installed = InstallRow(row, old, ref last, ordinal, preservePosition: previous is null);
            if (row.DrawCursor > 0 && old?.Draw?.Secret is { } stream && ReferenceEquals(stream, installed.Draw?.Secret)) {
                installed = installed with { Cells = old.Cells };
            } else if (previous is not null && row.DrawCursor > 0 && !row.IsKeyed) {
                installed = installed with { Cells = null };
            }
            rows ??= new(definition.AuthoredState);
            rows[index] = installed;
        }
        if (rows is not null) { definition = definition.WithWorldState(rows); }
        return WithLast(definition, last);
    }

    /// <summary>Updates the private-site allocator on a speculative document. Refused edits drop the allocation.</summary>
    public static WorldDefinition WithLast(WorldDefinition definition, long last) =>
        (definition.StateRaw?.DrawInstallCount ?? 0) == last ? definition :
        definition.WithStateSection((definition.StateRaw ?? new()) with { DrawInstallCount = last });

    /// <summary>The installation door shared by first fill, rebuild, row edits and authority restore.</summary>
    public WorldStateRow InstallRow(WorldStateRow submitted, WorldStateRow? previous, ref long last, long? ordinal = null, bool preservePosition = false) {
        if (submitted.Draw is not { PrivateStream: true } draw) { return submitted; }
        var declaration = JsonSerializer.SerializeToUtf8Bytes(draw, WorldJsonContext.Authored.Draw);
        if (previous?.Draw is { Secret: not null } old &&
            declaration.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(old, WorldJsonContext.Authored.Draw))) {
            return submitted with { Draw = old, DrawCursor = previous.DrawCursor, DrawnMasks = previous.DrawnMasks };
        }
        var installation = ordinal ?? checked(++last);
        return submitted with {
            Draw = InstallStream(draw, installation),
            DrawCursor = preservePosition ? submitted.DrawCursor : 0,
            DrawnMasks = preservePosition ? submitted.DrawnMasks : null
        };
    }
}
