using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Puck.Abstractions;
using Puck.World.Authoring;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// A placement's deal facet: the row is a template whose instances are dealt from a keyed state row, one child
/// placement per cell, laid out over the template's own <see cref="WorldPlacement.Distribution"/> region in cell
/// order. The template itself renders nothing and collides with nothing; its prototype, its
/// <see cref="WorldPlacement.Scale"/> and its
/// <see cref="WorldPlacement.Solid"/>/<see cref="WorldPlacement.Grip"/>/<see cref="WorldPlacement.Region"/>/
/// <see cref="WorldPlacement.Emission"/> facets are what every child is stamped with. The region's offsets are metres
/// in the template's frame, never scaled by it. A child is an ordinary
/// placement row named <c>&lt;template&gt;/&lt;cellKey&gt;</c> (<see cref="ChildId"/>) with
/// <see cref="WorldPlacement.Parent"/> naming the template and <see cref="WorldPlacement.DealSlot"/> its slot. Its
/// <see cref="WorldPlacement.Position"/> is its displacement from that slot, so its local frame is the slot's
/// fixed-point offset (<see cref="SlotOffset"/>) plus that displacement, composed by
/// <see cref="WorldPlacementFrameCompilation"/>, and moving a child to another slot moves it with its displacement. It is
/// installed by the per-tick sweep (<c>Server.WorldTick.SweepPlacementDeals</c>) through the shared placement
/// projection install, which refreshes colliders and delivery. The source cells journal, undo and replay; their
/// derived children are reprojected without adding declaration edits to the journal.
/// </summary>
/// <remarks>
/// <para>A child keeps its allocated <see cref="WorldPlacement.DealSlot"/> while its cell is present. Instance-owned
/// transforms may move away from that slot without releasing it. A cell that leaves frees its
/// slot and moves no sibling, and the next cell dealt takes the lowest free slot. The alternative — a cell's
/// ordinal among the cells present — would shuffle every later instance whenever an earlier cell left, and a court
/// whose accounts come and go would rebuild itself on every departure. The offsets are the same ones a static
/// distribution of the template materializes (<see cref="Offsets"/>), so a new instance starts where the copy
/// would have. <see cref="Preserve"/> controls subsequent reconciliation with the template.</para>
/// <para>The sweep re-deals when its source rows, template or placement projection changes. Source membership owns
/// child presence: removing or undoing a child alone is reconciled on that tick, independently of hidden edits.</para>
/// <para>A child is a reading of the cells it was dealt from, so a reader is shown it as the deal would deal it from
/// the rows that reader may read (<see cref="WorldStateDisclosure.Disclose"/>, the wire projection and a seat's console
/// alike): a template whose row can withhold from the reader is re-dealt in the reader's row order, each child moved
/// to its re-dealt slot with its own displacement, so the slots, positions and order it is shown carry nothing of the
/// authority's slot history; a withheld cell is absent, or an anonymous back under
/// <see cref="HiddenCells.Placeholder"/>, and a placement authored under a child the reader is not shown is absent
/// too; a withheld variant cell shows the template's own prototype, whether or not the dealt row withholds. Because
/// whatever a placement under such a template carries beyond that reading acts or shows at the authority's slot, a
/// template whose dealt or variant row can withhold from any principal, its children and every placement authored
/// under them carry nothing outside <see cref="UndisclosingMembers"/>' disclosing set.</para>
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
    /// <summary>The generated head of a placeholder's key (<see cref="PlaceholderId"/>): a key no author door admits,
    /// since an authored key carries no <see cref="GeneratedName.Joiner"/>, so a placeholder never names a dealt
    /// cell.</summary>
    public const string PlaceholderHead = "$back";

    /// <summary>Returns the id a child dealt for <paramref name="key"/> under <paramref name="template"/> carries.</summary>
    /// <param name="template">The template placement's id.</param>
    /// <param name="key">The dealt cell's key.</param>
    public static string ChildId(string template, string key) => string.Concat(
        str0: template,
        str1: ChildSeparator.ToString(),
        str2: key
    );
    /// <summary>Returns the id of the anonymous card back a recipient's view deals at <paramref name="slot"/> under
    /// <paramref name="template"/> for a cell its <see cref="HiddenCells.Placeholder"/> policy withholds:
    /// <c>&lt;template&gt;/$back$&lt;slot&gt;</c>. It names no key, so it says nothing of the cell but its place in
    /// the disclosed order. Only a recipient's view holds such a row; the authority has none, so a pick answers the
    /// template (<see cref="PickedId"/>) and a submission naming one is refused as a placeholder.</summary>
    /// <param name="template">The template placement's id.</param>
    /// <param name="slot">The slot the view dealt the back at.</param>
    public static string PlaceholderId(string template, int slot) => ChildId(
        template: template,
        key: GeneratedName.Join(PlaceholderHead, slot.ToString(provider: CultureInfo.InvariantCulture))
    );
    /// <summary>Returns whether <paramref name="id"/> is a placeholder's id (<see cref="PlaceholderId"/>), and the
    /// template it stands under.</summary>
    /// <param name="id">The placement id.</param>
    /// <param name="template">The template id, when it is one.</param>
    public static bool IsPlaceholderId(string? id, out string template) {
        template = string.Empty;
        if ((id is null) || !TrySplitChildId(id: id, template: out var parent, key: out var key) ||
            !GeneratedName.TryStripHead(name: key, head: PlaceholderHead, rest: out _)) {
            return false;
        }
        template = parent;
        return true;
    }
    /// <summary>Returns the placement id a pick on <paramref name="id"/> answers: the template's id for a placeholder,
    /// which is a look and no target, and <paramref name="id"/> itself for every other row.</summary>
    /// <param name="id">The picked row's id.</param>
    public static string? PickedId(string? id) => (IsPlaceholderId(id: id, template: out var template) ? template : id);
    /// <summary>Returns a fresh child as the deal stamps it at <paramref name="slot"/>: the template's prototype or a
    /// variant, its scale and its copied facets, parented to the template, standing exactly on its slot (no
    /// displacement, no yaw). The sweep deals every new child through it and a recipient's view every placeholder, so
    /// the two stamp one shape.</summary>
    /// <param name="template">The template placement.</param>
    /// <param name="id">The child's id.</param>
    /// <param name="prototype">The prototype the child shows.</param>
    /// <param name="slot">The slot.</param>
    public static WorldPlacement Child(WorldPlacement template, string id, string prototype, int slot) => new(
        Id: id,
        PrototypeId: prototype,
        Position: System.Numerics.Vector3.Zero,
        YawDegrees: 0f,
        Scale: template.Scale,
        Emission: template.Emission,
        Solid: template.Solid,
        Region: template.Region,
        Grip: template.Grip,
        Parent: template.Id,
        DealSlot: slot,
        Spatial: template.Spatial,
        Surface: template.Surface
    );
    /// <summary>Returns <paramref name="child"/> as this deal holds it at <paramref name="slot"/>: the stamp
    /// (<see cref="Child"/>) with <paramref name="prototype"/>, except for what <see cref="Preserve"/> names, which
    /// keeps the child's own (its displacement, yaw and scale; its prototype; its other facets). The sweep reconciles
    /// every kept child through it, and a recipient's reading of a template that can withhold shows each child through
    /// it, so a member the template owns is shown as the sweep will hold it, never a value a mover wrote that the next
    /// re-deal (whose timing hidden cells can decide) resets.</summary>
    /// <param name="template">The template placement carrying this deal.</param>
    /// <param name="child">The child as it stands.</param>
    /// <param name="prototype">The prototype the deal resolves for the child's cell.</param>
    /// <param name="slot">The slot the child holds.</param>
    /// <returns>The reconciled child.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> or <paramref name="child"/> is
    /// <see langword="null"/>.</exception>
    public WorldPlacement Reconcile(WorldPlacement template, WorldPlacement child, string prototype, int slot) {
        ArgumentNullException.ThrowIfNull(argument: template);
        ArgumentNullException.ThrowIfNull(argument: child);

        var seed = Child(template: template, id: child.Id, prototype: prototype, slot: slot);
        var transform = (Preserve?.Transform == true);

        return ((Preserve?.Facets == true) ? child : seed) with {
            Parent = template.Id,
            DealSlot = slot,
            Deal = null,
            Position = (transform ? child.Position : seed.Position),
            YawDegrees = (transform ? child.YawDegrees : seed.YawDegrees),
            Scale = (transform ? child.Scale : seed.Scale),
            PrototypeId = ((Preserve?.Prototype == true) ? child.PrototypeId : prototype),
        };
    }

    // The placement members a template that can withhold, its dealt children and every placement authored under them
    // may carry, by JSON name: what the re-deal already discloses (the identity and parent, the prototype the disclosed
    // face selects, the child's own displacement, yaw and scale, which the re-deal carries to the re-dealt slot) and
    // presentation that composes over that transform. Every other member, a member added to WorldPlacement later included, is refused.
    private static readonly HashSet<string> Disclosing = new(collection: ["id", "prototypeId", "position", "yawDegrees", "scale", "parent", "dealSlot", "distribution", "mirror", "surface"], comparer: StringComparer.Ordinal);

    /// <summary>Returns, by JSON name, every member <paramref name="placement"/> carries beyond what a re-deal discloses,
    /// the members a placement under a template whose dealt or variant row can withhold may not carry. Only the
    /// identity, parent and slot, the prototype, the transform, a distribution, a mirror and a surface projection
    /// compose over the transform a recipient's re-deal shows; anything else (a collider, a hold, a census volume, a
    /// region, a voice, a body, an attachment, a face source, a response, a board, a contribution, and any member the
    /// placement model gains later) acts or shows at the authority's own slot, which hidden arrivals shape, so it is an
    /// oracle no view can close. The members are read from the generated model shape
    /// (<see cref="WorldModelShape"/>), so a new member is refused until it is added to the disclosing set.</summary>
    /// <param name="placement">The template, a dealt child, or a placement authored under one.</param>
    /// <param name="template">Whether <paramref name="placement"/> is the template itself, the one row that carries
    /// the <c>deal</c> member.</param>
    /// <returns>The undisclosing members' JSON names, in model order; empty when <paramref name="placement"/> carries
    /// none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    public static IEnumerable<string> UndisclosingMembers(WorldPlacement placement, bool template) {
        ArgumentNullException.ThrowIfNull(argument: placement);

        foreach (var member in WorldModelShape.Of(type: typeof(WorldPlacement))!.Members) {
            if ((member.Get is not { } read) || ((member.Access & WorldModelAccess.Write) == 0) || Disclosing.Contains(item: member.Name) ||
                (template && string.Equals(a: member.Name, b: "deal", comparisonType: StringComparison.Ordinal))) {
                continue;
            }
            if (Carries(value: read(arg: placement))) { yield return member.Name; }
        }
    }
    // Whether a member's value is set: absent, a zero or false scalar and an empty collection carry nothing; any other
    // value, a struct this does not know included, carries something.
    private static bool Carries(object? value) => value switch {
        null => false,
        bool flag => flag,
        int number => (number != 0),
        long number => (number != 0L),
        float number => (number != 0f),
        double number => (number != 0d),
        string text => (text.Length != 0),
        Enum flag => (Convert.ToInt64(value: flag, provider: CultureInfo.InvariantCulture) != 0L),
        System.Collections.IEnumerable items => items.GetEnumerator().MoveNext(),
        _ => true,
    };
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
            (span.Length == ((template.Length + 1) + key.Length)) &&
            span.StartsWith(
            value: template.AsSpan(),
            comparisonType: StringComparison.Ordinal
        ) &&
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

        if (
            (separator <= 0) ||
            (separator >= (id.Length - 1))
        ) {
            template = string.Empty;
            key = string.Empty;

            return false;
        }

        template = id.Substring(
            length: separator,
            startIndex: 0
        );
        key = id.Substring(startIndex: (separator + 1));

        return true;
    }

    // A distribution's offsets under one seed, derived once per distribution row (a noise region resolves its whole
    // grid) and shared by every frame, sweep, re-deal and reflow that reads a slot. Bound lattice steps bypass it.
    private sealed record OffsetEntry(ulong WorldSeed, ImmutableArray<FixedVector3> Offsets);

    private static readonly CodeDerivedTable<WorldDistribution, OffsetEntry> OffsetCache = new();

    /// <summary>Returns the placement-local offsets a template's region deals, in deal order — the identical set and order a
    /// static distribution of the same row materializes (<see cref="WorldPlacementStamp.SampledFixedOffsetsFor"/> for
    /// Noise/Scatter; a Lattice's A-major, B-minor grid, matching <c>CreationStampLattice.ForEachFixedInstance</c>).
    /// Empty when the template carries no distribution or a region that materializes no fixed count. Derived once per
    /// literal distribution row and seed; state-bound lattice steps derive from their current values on each read.</summary>
    /// <param name="template">The template placement row.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    public static ImmutableArray<FixedVector3> Offsets(WorldPlacement template, ulong worldSeed) {
        if (template.Distribution is not { } distribution) {
            return [];
        }
        // The immutable distribution identity does not identify a live step's value.
        if ((distribution.Region is WorldDistributionRegion.Lattice { } lattice) &&
            ((lattice.StepA.Reference is not null) || (lattice.StepB.Reference is not null))) {
            return DeriveOffsets(template: template, worldSeed: worldSeed);
        }
        if (OffsetCache.TryGetValue(key: distribution, value: out var held) && (held.WorldSeed == worldSeed)) {
            return held.Offsets;
        }

        var derived = new OffsetEntry(WorldSeed: worldSeed, Offsets: DeriveOffsets(template: template, worldSeed: worldSeed));

        OffsetCache.AddOrUpdate(key: distribution, value: derived);
        return derived.Offsets;
    }
    /// <summary>Returns the offset of <paramref name="slot"/> in <paramref name="template"/>'s region, in the template's
    /// frame and in fixed point: where a child dealt at that slot stands before its own displacement. A slot the region
    /// does not hold (a document validation has not yet refused) answers the template's origin.</summary>
    /// <param name="template">The template placement row.</param>
    /// <param name="slot">The child's deal slot.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    public static FixedVector3 SlotOffset(WorldPlacement template, int? slot, ulong worldSeed) {
        var offsets = Offsets(template: template, worldSeed: worldSeed);

        return ((slot is { } index) && (((uint)index) < ((uint)offsets.Length))) ? offsets[index] : FixedVector3.Zero;
    }
    private static ImmutableArray<FixedVector3> DeriveOffsets(WorldPlacement template, ulong worldSeed) {
        if (template.Distribution?.Region is WorldDistributionRegion.Lattice lattice) {
            var countA = Math.Max(
                val1: lattice.CountA,
                val2: 1
            );
            var countB = Math.Max(
                val1: lattice.CountB,
                val2: 1
            );
            var stepA = FixedVector3.FromVector3(value: lattice.StepA);
            var stepB = FixedVector3.FromVector3(value: lattice.StepB);
            var offsets = new FixedVector3[(countA * countB)];
            var index = 0;

            for (var indexA = 0; (indexA < countA); indexA++) {
                for (var indexB = 0; (indexB < countB); indexB++) {
                    offsets[index++] = ((stepA * FixedQ4816.FromInteger(value: indexA)) + (stepB * FixedQ4816.FromInteger(value: indexB)));
                }
            }

            return ImmutableCollectionsMarshal.AsImmutableArray(array: offsets);
        }

        return ((WorldPlacementStamp.SampledFixedOffsetsFor(
            placement: template,
            worldSeed: worldSeed
        ) is { } sampled)
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
        WorldDistributionRegion.Lattice lattice => (Math.Max(
        val1: lattice.CountA,
        val2: 1
    ) * Math.Max(
        val1: lattice.CountB,
        val2: 1
    )),
        WorldDistributionRegion.Scatter scatter => checked((int)CreationStampSampling.ScatterInstanceCeiling(
        width: scatter.Width,
        depth: scatter.Depth,
        spacing: scatter.Spacing
    )),
        WorldDistributionRegion.Noise => Offsets(
        template: template,
        worldSeed: worldSeed
    ).Length,
        _ => 0,
    };
    /// <summary>Returns the prototype a child dealt for <paramref name="key"/> shows: the variant row's same-keyed cell
    /// selects from <see cref="WorldPlacementDealVariants.Map"/> by its text, or by its raw integer value spelled as
    /// text; no variants, no variant row, no cell, or no entry deals <paramref name="templatePrototype"/>. The deal
    /// sweep reads every child's prototype through this one resolution.</summary>
    /// <param name="arena">The live store holding the variant row.</param>
    /// <param name="variantOrdinal">The variant row's ordinal in <paramref name="arena"/>, or a negative value when the
    /// deal names no variant row the store declares.</param>
    /// <param name="key">The dealt cell's key.</param>
    /// <param name="templatePrototype">The template's own prototype.</param>
    /// <returns>The prototype id the child shows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public string ResolvePrototype(StateArena arena, int variantOrdinal, string key, string templatePrototype) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if ((variantOrdinal < 0) || (Variants?.Map is not { } map) || !CellName.TryParse(key, out var name, out _) ||
            !arena.Keys.TryResolve(name, out var cell) || !arena.TryRead(variantOrdinal, cell, out var value)) {
            return templatePrototype;
        }
        if (value.Kind == CellKind.Text) {
            return (map.TryGetValue(key: value.AsText, value: out var selected) ? selected : templatePrototype);
        }
        foreach (var (spelling, prototype) in map) {
            if (long.TryParse(s: spelling, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var raw) && (raw == value.Raw)) {
                return prototype;
            }
        }
        return templatePrototype;
    }
    /// <summary>Returns a definition in which every dealt child shows the costliest prototype its deal can deal: the
    /// template's own prototype or any prototype its variant map selects, whichever stamps the most instances per copy.
    /// A reader whose disclosure withholds a child's variant cell is dealt the template's prototype, and another reader
    /// sees the selected variant, so this is the upper bound a measurement sizing for every reader counts. Only a
    /// measurement reads it; nothing renders it.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The rewritten definition, or <paramref name="definition"/> itself when no child changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldDefinition WithCostliestChildren(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var placements = definition.Placements;
        List<WorldPlacement>? rewritten = null;

        for (var index = 0; (index < placements.Count); index++) {
            var placement = placements[index];

            if (
                (placement.Parent is not { } parentId) ||
                (WorldDefinitionRows.FindPlacement(
                id: parentId,
                placements: placements
            ) is not { Deal: { } deal } parent) ||
                !IsChild(
                parent: parent,
                placement: placement
            )
            ) {
                continue;
            }

            var costliest = placement.PrototypeId;
            var most = -1;

            foreach (var candidate in ((string[])[parent.PrototypeId, .. (deal.Variants?.Map.Values ?? [])])) {
                if (WorldDefinitionRows.FindCreation(
                    creations: definition.Creations,
                    id: candidate
                ) is not { } creation) {
                    continue;
                }

                var count = CreationStampEmitter.PerCopyInstanceCount(document: creation.Document);

                if (count > most) {
                    most = count;
                    costliest = candidate;
                }
            }

            if (!string.Equals(
                a: costliest,
                b: placement.PrototypeId,
                comparisonType: StringComparison.Ordinal
            )) {
                rewritten ??= [.. placements];
                rewritten[index] = (placement with { PrototypeId = costliest });
            }
        }

        return ((rewritten is null)
            ? definition
            : (definition with { PlacementRowsRaw = rewritten }));
    }
    /// <summary>Returns whether <paramref name="placement"/> is a dealt child: it names a parent carrying a deal facet and
    /// spells that parent's child id shape.</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="parent">The row <paramref name="placement"/>'s <see cref="WorldPlacement.Parent"/> resolves to,
    /// or <see langword="null"/>.</param>
    public static bool IsChild(WorldPlacement placement, WorldPlacement? parent) => (
        (parent is { Deal: not null }) &&
        (placement.Id is not null) &&
        (placement.Parent is { } parentId) &&
        string.Equals(
        a: parentId,
        b: parent.Id,
        comparisonType: StringComparison.Ordinal
    ) &&
        (placement.Id.Length > (parentId.Length + 1)) &&
        placement.Id.AsSpan().StartsWith(
        value: parentId.AsSpan(),
        comparisonType: StringComparison.Ordinal
    ) &&
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
/// <param name="Transform">Preserve an existing child's displacement from its slot, yaw, and scale.</param>
/// <param name="Prototype">Preserve its prototype, including changes made by responses.</param>
/// <param name="Facets">Preserve its other facets, including responses and face sources.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementDealPreserve(bool Transform = false, bool Prototype = false, bool Facets = false);
