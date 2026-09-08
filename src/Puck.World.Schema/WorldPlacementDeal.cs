using System.Text.Json.Serialization;
using Puck.World.Authoring;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// A placement's deal facet: the row is a template whose instances are dealt from a keyed state row, one child
/// placement per cell, laid out over the template's own <see cref="WorldPlacement.Distribution"/> region in cell
/// order. The template itself renders nothing and collides with nothing; its prototype and its
/// <see cref="WorldPlacement.Solid"/>/<see cref="WorldPlacement.Grip"/>/<see cref="WorldPlacement.Region"/>/
/// <see cref="WorldPlacement.Emission"/> facets are what every child is stamped with. A child is an ordinary
/// placement row named <c>&lt;template&gt;/&lt;cellKey&gt;</c> (<see cref="ChildId"/>) with
/// <see cref="WorldPlacement.Parent"/> naming the template and the region's dealt offset as its local position,
/// written and removed through ordinary <c>UpsertPlacement</c>/<c>RemovePlacement</c> mutations under
/// <c>WorldPrincipal.World</c> by the per-tick sweep (<c>Server.WorldServer.SweepPlacementDeals</c>), so a dealt
/// instance journals, undoes, replays, and rebuilds colliders through the one placement door.
/// </summary>
/// <remarks>
/// <para>A child keeps its allocated <see cref="WorldPlacement.DealSlot"/> while its cell is present. Instance-owned
/// transforms may move away from that slot without releasing it. A cell that leaves frees its
/// slot and moves no sibling, and the next cell dealt takes the lowest free slot. The alternative — a cell's
/// ordinal among the cells present — would shuffle every later instance whenever an earlier cell left, and a court
/// whose accounts come and go would rebuild itself on every departure. The offsets are the same ones a static
/// distribution of the template materializes (<see cref="Offsets"/>), so a new instance starts where the copy
/// would have. <see cref="Preserve"/> controls subsequent reconciliation with the template.</para>
/// <para>The sweep re-deals only when the dealt row, the variant row, or the template itself changes: an undo that
/// removes the children a sweep added stays undone until the row moves again.</para>
/// <para>Refused alongside <see cref="WorldPlacement.Inhabit"/>, <see cref="WorldPlacement.Attach"/>,
/// <see cref="WorldPlacement.Respond"/>, <see cref="WorldPlacement.Mirror"/>, and
/// <see cref="WorldPlacement.FaceSources"/>; requires a <see cref="WorldPlacement.Distribution"/> whose region
/// materializes a fixed instance count (Lattice, Noise, Scatter — never Disc or Points), at least the dealt row's
/// capacity. An authored placement id spelling <see cref="ChildSeparator"/> is refused by name: only the sweep mints
/// one.</para>
/// </remarks>
/// <param name="Row">The keyed <c>state.world</c> row whose cells are dealt, of any cell kind. One child per cell,
/// keyed by the cell's key.</param>
/// <param name="Variants">The optional variant selection: a second keyed row read at the same key, whose cell text
/// (an integer cell spelled as text) selects a prototype from <see cref="WorldPlacementDealVariants.Map"/>; a cell
/// with no entry, or no cell at all, deals the template's own prototype.</param>
/// <param name="Preserve">Which properties of existing instances are owned by gameplay. Absent synchronizes
/// copied properties with the template. New instances receive the template's supported copied facets.</param>
/// <param name="Reflow">Optional bounded rearrangement policy. Requires instance-owned transforms and a footprint.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementDeal(
    string Row,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementDealVariants? Variants = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementDealPreserve? Preserve = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementReflow? Reflow = null
) {
    /// <summary>The character between a template's id and the cell key in a dealt child's id.</summary>
    public const char ChildSeparator = '/';

    /// <summary>Returns the id a child dealt for <paramref name="key"/> under <paramref name="template"/> carries.</summary>
    /// <param name="template">The template placement's id.</param>
    /// <param name="key">The dealt cell's key.</param>
    public static string ChildId(string template, string key) => string.Concat(str0: template, str1: ChildSeparator.ToString(), str2: key);
    /// <summary>Returns whether <paramref name="id"/> spells <see cref="ChildSeparator"/> at all — the shape only a deal sweep
    /// may mint.</summary>
    /// <param name="id">The placement id.</param>
    public static bool IsChildId(string id) => (id.IndexOf(value: ChildSeparator) >= 0);
    /// <summary>Returns whether <paramref name="id"/> is exactly the child id <see cref="ChildId"/> forms for
    /// <paramref name="template"/> and <paramref name="key"/>, without allocating the composed string.</summary>
    /// <param name="id">The placement id.</param>
    /// <param name="template">The template placement's id.</param>
    /// <param name="key">The dealt cell's key.</param>
    public static bool IsChildOf(string id, string template, ReadOnlySpan<char> key) {
        var span = id.AsSpan();

        return (
            (span.Length == (template.Length + 1 + key.Length)) &&
            span.StartsWith(value: template.AsSpan(), comparisonType: StringComparison.Ordinal) &&
            (span[template.Length] == ChildSeparator) &&
            span.Slice(start: (template.Length + 1)).SequenceEqual(other: key)
        );
    }
    /// <summary>Splits a child id into its template id and cell key at the first <see cref="ChildSeparator"/>.</summary>
    /// <param name="id">The placement id.</param>
    /// <param name="template">The template id before the separator.</param>
    /// <param name="key">The cell key after it.</param>
    /// <returns><see langword="false"/> when <paramref name="id"/> spells no separator, or spells it first or last.</returns>
    public static bool TrySplitChildId(string id, out string template, out string key) {
        var separator = id.IndexOf(value: ChildSeparator);

        if ((separator <= 0) || (separator >= (id.Length - 1))) {
            template = string.Empty;
            key = string.Empty;

            return false;
        }

        template = id.Substring(startIndex: 0, length: separator);
        key = id.Substring(startIndex: (separator + 1));

        return true;
    }
    /// <summary>Returns the placement-local offsets a template's region deals, in deal order — the identical set and order a
    /// static distribution of the same row materializes (<see cref="WorldPlacementStamp.SampledFixedOffsetsFor"/> for
    /// Noise/Scatter; a Lattice's A-major, B-minor grid, matching <c>CreationStampLattice.ForEachFixedInstance</c>).
    /// Empty when the template carries no distribution or a region that materializes no fixed count.</summary>
    /// <param name="template">The template placement row.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    public static FixedVector3[] Offsets(WorldPlacement template, ulong worldSeed) {
        if (template.Distribution?.Region is WorldDistributionRegion.Lattice lattice) {
            var countA = Math.Max(val1: lattice.CountA, val2: 1);
            var countB = Math.Max(val1: lattice.CountB, val2: 1);
            var stepA = FixedVector3.FromVector3(value: lattice.StepA);
            var stepB = FixedVector3.FromVector3(value: lattice.StepB);
            var offsets = new FixedVector3[(countA * countB)];
            var index = 0;

            for (var indexA = 0; (indexA < countA); indexA++) {
                for (var indexB = 0; (indexB < countB); indexB++) {
                    offsets[index++] = ((stepA * FixedQ4816.FromInteger(value: indexA)) + (stepB * FixedQ4816.FromInteger(value: indexB)));
                }
            }

            return offsets;
        }

        return ((WorldPlacementStamp.SampledFixedOffsetsFor(placement: template, worldSeed: worldSeed) is { } sampled)
            ? [.. sampled]
            : []
        );
    }
    /// <summary>Returns the number of instances a template's region deals — <see cref="Offsets"/>' length, the exact count
    /// (seed-resolved for Noise) the dealt row's capacity is bounded by and <c>world.budget</c> folds into the
    /// placement census.</summary>
    /// <param name="template">The template placement row.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    public static int InstanceCount(WorldPlacement template, ulong worldSeed) => template.Distribution?.Region switch {
        WorldDistributionRegion.Lattice lattice => (Math.Max(val1: lattice.CountA, val2: 1) * Math.Max(val1: lattice.CountB, val2: 1)),
        WorldDistributionRegion.Scatter scatter => checked((int)CreationStampSampling.ScatterInstanceCeiling(width: scatter.Width, depth: scatter.Depth, spacing: scatter.Spacing)),
        WorldDistributionRegion.Noise => Offsets(template: template, worldSeed: worldSeed).Length,
        _ => 0,
    };
    /// <summary>Returns whether <paramref name="placement"/> is a dealt child: it names a parent carrying a deal facet and
    /// spells that parent's child id shape.</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="parent">The row <paramref name="placement"/>'s <see cref="WorldPlacement.Parent"/> resolves to,
    /// or <see langword="null"/>.</param>
    public static bool IsChild(WorldPlacement placement, WorldPlacement? parent) => (
        (parent is { Deal: not null }) &&
        (placement.Id is not null) &&
        (placement.Parent is { } parentId) &&
        string.Equals(a: parentId, b: parent.Id, comparisonType: StringComparison.Ordinal) &&
        (placement.Id.Length > (parentId.Length + 1)) &&
        placement.Id.AsSpan().StartsWith(value: parentId.AsSpan(), comparisonType: StringComparison.Ordinal) &&
        (placement.Id[parentId.Length] == ChildSeparator)
    );
}
/// <summary>A deal facet's variant selection: a keyed row read at the dealt cell's key, and the map from that cell's
/// text to the prototype the child is dealt with.</summary>
/// <param name="Row">The keyed <c>state.world</c> row read at the dealt cell's key. May be the dealt row itself.</param>
/// <param name="Map">Cell text (an integer cell's value spelled as text) to the <see cref="WorldPrototype.Id"/> the
/// child shows. Every value must resolve to a declared, non-animated creation.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementDealVariants(string Row, IReadOnlyDictionary<string, string> Map);

/// <summary>Instance-owned properties which reconciliation seeds at creation and subsequently preserves.
/// Membership and the allocated deal slot remain owned by the source row.</summary>
/// <param name="Transform">Preserve an existing child's local position, yaw, and scale.</param>
/// <param name="Prototype">Preserve its prototype, including changes made by responses.</param>
/// <param name="Facets">Preserve its other facets, including responses and face sources.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementDealPreserve(bool Transform = false, bool Prototype = false, bool Facets = false);
