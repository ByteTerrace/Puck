using System.Security.Cryptography;
using System.Text.Json;

namespace Puck.World;

/// <summary>The installation ordinals of a document's private streams. These addresses carry no keys.</summary>
public sealed record WorldDrawPositions(long Last, Dictionary<string, long> Sites);

/// <summary>One world's sampler. Only authority persistence receives its seed. Replays consume recorded words.</summary>
public sealed class WorldPrivateDrawKeys : PrivateDrawAuthority {
    private readonly bool m_authority;
    private WorldDefinition? m_savedRows;

    private WorldPrivateDrawKeys(byte[] seed, bool authority) : base(seed) {
        m_authority = authority;
    }

    /// <summary>A fresh authority lifetime.</summary>
    public static WorldPrivateDrawKeys Create() => new(RandomNumberGenerator.GetBytes(32), true);

    /// <summary>Creates another lifetime of the same kind for a named instance.</summary>
    public WorldPrivateDrawKeys NewInstance() => m_authority ? Create() : Preview;

    /// <summary>A deterministic isolated sampler for proofs and tests.</summary>
    public static WorldPrivateDrawKeys Preview => new(new byte[32], false);

    /// <summary>Restores the seed carried by this world's authority state.</summary>
    public static WorldPrivateDrawKeys FromAuthorityState(byte[] seed) =>
        seed.Length == 32 && seed.AsSpan().IndexOfAnyExcept((byte)0) >= 0
            ? new(seed, true) : throw new InvalidDataException("Invalid private draw authority state.");

    /// <summary>Restores saved private rows before their next first-fill pass.</summary>
    public void ResumeSave(WorldStateSection rows, WorldDrawPositions positions) =>
        m_savedRows = Install(new WorldDefinition(StateRaw: rows), restore: positions);

    internal WorldDefinition InstallAtBoot(WorldDefinition definition) => Install(definition, previous: m_savedRows, resumeCells: true);

    /// <summary>Returns a copy for the authority's private store.</summary>
    public byte[]? SerializeAuthorityState() => m_authority ? CopySeed() : null;

    /// <summary>Records the nonsecret stream addresses beside an authority or replay document.</summary>
    public static WorldDrawPositions? Positions(WorldDefinition definition) {
        if (definition.StateRaw is not { DrawInstallCount: > 0 } state) { return null; }
        return new(state.DrawInstallCount, definition.AuthoredState.Where(r => r.Draw?.Secret is not null)
            .ToDictionary(r => r.Name.Value, r => r.Draw!.Secret!.Installation, StringComparer.Ordinal));
    }

    /// <summary>Binds declarations to the world's streams. A writer can supply neither an installation ordinal
    /// nor a position in a retained stream. Restore takes its ordinals from the authority continuation.</summary>
    public WorldDefinition Install(WorldDefinition definition, WorldDefinition? previous = null, WorldDrawPositions? restore = null, bool resumeCells = false) {
        var last = restore?.Last ?? previous?.StateRaw?.DrawInstallCount ?? 0L;
        List<WorldStateRow>? rows = null;
        for (var index = 0; index < definition.AuthoredState.Count; index++) {
            var row = definition.AuthoredState[index];
            if (row.Draw?.PrivateStream != true) { continue; }
            long? ordinal = null;
            if (restore is not null) {
                if (!restore.Sites.TryGetValue(row.Name.Value, out var id) || id <= 0 || id > last) {
                    throw new InvalidDataException("Invalid private draw installation in the authority continuation.");
                }
                ordinal = id;
            }
            var old = previous is null ? null : WorldDefinitionRows.FindStateRow(previous.AuthoredState, row.Name.Value);
            var installed = InstallRow(row, old, ref last, ordinal, preservePosition: previous is null);
            if ((resumeCells || row.DrawCursor > 0) && old?.Draw?.Secret is { } stream && ReferenceEquals(stream, installed.Draw?.Secret)) {
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
