using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>
/// The palettes one static emission registers, kept by the owner that rebuilds its static program
/// (<see cref="WorldPlacementStamper.EmitStatic"/>) so a rebuild reuses them: each creation's material ids live in an
/// array kept by its id and refilled once per emission, so an untinted creation's palette is registered once however
/// many placements show it, and a warm emission allocates no dictionary and no id array. An array is replaced only when
/// its creation's palette changes length.
/// </summary>
public sealed class WorldStaticPalettes {
    private readonly Dictionary<string, int[]> m_ids = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_filled = new(comparer: StringComparer.Ordinal);

    /// <summary>Starts an emission: every palette is registered again on its first use.</summary>
    public void Begin() => m_filled.Clear();
    /// <summary>Returns the material ids of an untinted creation's palette, registering it with
    /// <paramref name="builder"/> on its first use since <see cref="Begin"/>.</summary>
    /// <param name="builder">The program builder the emission writes.</param>
    /// <param name="colors">The colors the build bakes.</param>
    /// <param name="creation">The creation row.</param>
    /// <returns>The ids, indexed like the creation's palette slots; valid until the next <see cref="Begin"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public int[] Resolve(SdfProgramBuilder builder, WorldBakedColors colors, WorldPrototype creation) {
        ArgumentNullException.ThrowIfNull(argument: builder);
        ArgumentNullException.ThrowIfNull(argument: colors);
        ArgumentNullException.ThrowIfNull(argument: creation);

        var length = WorldPlacementStamper.PaletteLength(document: creation.Document);

        if (
            !m_ids.TryGetValue(
                key: creation.Id,
                value: out var ids
            ) ||
            (ids.Length != length)
        ) {
            ids = new int[length];
            m_ids[creation.Id] = ids;
            _ = m_filled.Remove(item: creation.Id);
        }
        if (m_filled.Add(item: creation.Id)) {
            WorldPlacementStamper.FillPalette(
                builder: builder,
                colors: colors,
                document: creation.Document,
                ids: ids,
                tint: null
            );
        }

        return ids;
    }
}
