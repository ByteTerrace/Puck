using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The installation ordinals of a document's private streams. These addresses carry no keys.</summary>
public sealed record WorldDrawPositions([property: JsonRequired] long Last, [property: JsonRequired] Dictionary<string, long> Sites) {
    [JsonIgnore]
    public bool IsValid => Last >= 0 && Sites is not null && Sites.All(p => !string.IsNullOrEmpty(p.Key) && p.Value > 0 && p.Value <= Last)
        && Sites.Values.Distinct().Count() == Sites.Count;
}

/// <summary>One activation's in-memory draw sampler.</summary>
public class WorldDrawSampler : PrivateDrawAuthority {
    protected WorldDrawSampler(byte[] seed, bool recordedOnly = false, bool canInstall = true, bool preview = false) : base(seed) {
        RecordedOnly = recordedOnly;
        CanInstall = canInstall;
        IsPreview = preview;
    }
    internal bool IsPreview { get; }
    protected override bool RecordedOnly { get; }
    protected override bool CanInstall { get; }
    /// <summary>An isolated deterministic sampler for authoring and proofs.</summary>
    public static WorldDrawSampler Preview => new(new byte[32], preview: true);
    /// <summary>An admission with no sampler supplied.</summary>
    public static WorldDrawSampler Missing => new(new byte[32], canInstall: false);

    /// <summary>Records the nonsecret stream addresses beside an authority or replay document.</summary>
    public static WorldDrawPositions? Positions(WorldDefinition definition) {
        if (definition.StateRaw is not { DrawInstallCount: > 0 } state) { return null; }
        return new(state.DrawInstallCount, definition.AuthoredState.Where(r => r.Draw?.Secret is not null)
            .ToDictionary(r => r.Name.Value, r => r.Draw!.Secret!.Installation, StringComparer.Ordinal));
    }

    /// <summary>Binds declarations to the world's streams. A writer can supply neither an installation ordinal
    /// nor a position in a retained stream. Restore takes its ordinals from the authority continuation.</summary>
    protected internal WorldDefinition Install(WorldDefinition definition, WorldDefinition? previous = null, WorldDrawPositions? restore = null) {
        var last = restore?.Last ?? previous?.StateRaw?.DrawInstallCount ?? 0L;
        List<WorldStateRow>? rows = null;
        for (var index = 0; index < definition.AuthoredState.Count; index++) {
            var row = definition.AuthoredState[index];
            if (row.Draw?.PrivateStream != true) { continue; }
            long? ordinal = null;
            if (restore is not null) {
                if (restore.Sites.TryGetValue(row.Name.Value, out var id) && id > 0 && id <= last) {
                    ordinal = id;
                } else {
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
    protected internal WorldStateRow InstallRow(WorldStateRow submitted, WorldStateRow? previous, ref long last, long? ordinal = null, bool preservePosition = false) {
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
