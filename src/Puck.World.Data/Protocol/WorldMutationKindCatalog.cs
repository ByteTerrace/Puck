using System.Reflection;

namespace Puck.World.Protocol;

/// <summary>Declares one <see cref="WorldMutation"/> nested record's stable DISPATCH ORDINAL and the
/// <see cref="WorldSection"/> it targets — the wire vocabulary <see cref="MutationKindMask"/> and the addon mutation
/// door (<c>Server.WorldAddonMutationDecoder</c>) read off. Every nested record under <see cref="WorldMutation"/>
/// carries exactly one of these, with an EXPLICIT ordinal (never inferred from declaration order — a reordered file
/// must never silently renumber the wire). <see cref="WorldMutationKindCatalog"/> discovers every so-tagged record by
/// reflection over this assembly and validates the whole set at boot, the same DISCOVERED-NOT-HAND-KEPT posture
/// <c>RefusalAttribute</c> uses for <c>world.refusals</c>.</summary>
/// <param name="ordinal">The kind's stable dispatch ordinal, <c>0..</c><see cref="WorldMutationKindCatalog.MaxOrdinal"/>
/// (one bit of the <see cref="MutationKindMask"/> lane). An ordinal past the lane is refused at boot rather than left
/// to wrap: .NET masks a shift count by the operand's width, so an out-of-lane bit aliases a REAL kind and would
/// admit the wrong door silently.</param>
/// <param name="section">The <see cref="WorldSection"/> this kind targets (must equal what
/// <c>Server.WorldServer.SectionOf</c> maps the SAME record type to — that cross-check is Server-side, since this
/// project cannot reference Server; this attribute only records the DECLARED pairing).</param>
[AttributeUsage(validOn: AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MutationKindAttribute(int ordinal, WorldSection section) : Attribute {
    /// <summary>Gets the kind's stable dispatch ordinal.</summary>
    public int Ordinal { get; } = ordinal;

    /// <summary>Gets the section this kind targets.</summary>
    public WorldSection Section { get; } = section;
}

/// <summary>One cataloged mutation kind: the nested record's CLR type, its declared ordinal, and its declared section.</summary>
/// <param name="Type">The nested <see cref="WorldMutation"/> record type.</param>
/// <param name="Ordinal">The declared dispatch ordinal.</param>
/// <param name="Section">The declared section.</param>
public readonly record struct WorldMutationKindCatalogEntry(Type Type, int Ordinal, WorldSection Section);

/// <summary>
/// Discovers, at boot, every <see cref="MutationKindAttribute"/>-tagged <see cref="WorldMutation"/> nested record in
/// this assembly, and validates the set: every ordinal in <c>0..</c><see cref="MaxOrdinal"/>, no two kinds sharing an ordinal, no kind
/// missing the attribute. A violation fails BOOT loudly (a <see cref="InvalidOperationException"/> thrown from
/// <see cref="Validate"/>) — an ordinal collision or an out-of-range value is a build-time authoring defect, never a
/// runtime data condition, so it must never reach a live session. This is the catalog only: it does NOT build a
/// <see cref="MutationKindMask"/> or the addon door's guest-reachable ordinal subset — see <see cref="KindsOf"/> for
/// the mask projection and <c>Server.WorldAddonMutationDecoder</c> for which ordinals a guest may actually dispatch.
/// </summary>
public static class WorldMutationKindCatalog {
    /// <summary>The highest ordinal a kind may declare — the top bit of the 128-bit
    /// <see cref="MutationKindMask"/> lane these pack into. An ordinal past the lane must be refused here rather than
    /// left to wrap: .NET masks a shift count by the operand's width, so on the former 64-bit lane
    /// <c>1UL &lt;&lt; 64</c> silently produced <c>1UL &lt;&lt; 0</c> and a 65th kind would have been admitted as
    /// <c>UpsertKit</c> — a grant quietly opening the wrong door rather than failing. The same wrap exists at 128 on
    /// the wider lane, so the ceiling moves with the lane and keeps the refusal loud.</summary>
    public const int MaxOrdinal = 127;

    private static IReadOnlyList<WorldMutationKindCatalogEntry>? s_entries;
    private static readonly MutationKindMask[] s_kindsBySection = new MutationKindMask[(Enum.GetValues<WorldSection>().Length)];
    private static bool s_kindsBySectionBuilt;

    /// <summary>Returns every cataloged mutation kind, sorted by ordinal. Discovered once, cached, and VALIDATED on first
    /// access (see <see cref="Validate"/> for what a violation does).</summary>
    /// <returns>The catalog.</returns>
    public static IReadOnlyList<WorldMutationKindCatalogEntry> All() {
        return (s_entries ??= Discover());
    }

    /// <summary>Returns the mask of every kind ordinal declared under <paramref name="section"/> — the ceiling a
    /// <see cref="WorldGrant.KindMask"/> row over that section's subject may legitimately name (see
    /// <c>Server.WorldGrants</c>'s grant-door remarks for why a bit outside this set is refused rather than
    /// silently admitted-and-inert). Built once, from the validated catalog, and cached.</summary>
    /// <param name="section">The section to project.</param>
    /// <returns>The section's kind mask.</returns>
    public static MutationKindMask KindsOf(WorldSection section) {
        if (!s_kindsBySectionBuilt) {
            foreach (var entry in All()) {
                s_kindsBySection[(int)entry.Section] = s_kindsBySection[(int)entry.Section].With(ordinal: entry.Ordinal);
            }

            s_kindsBySectionBuilt = true;
        }

        return s_kindsBySection[(int)section];
    }

    /// <summary>Returns the declared dispatch ordinal of one live mutation's own kind — the bit a
    /// <see cref="MutationKindMask"/> must carry for a grant row to admit it. THROWS rather than returning a
    /// sentinel: every nested kind carries an attribute (<see cref="Validate"/> proves it at boot), so a miss here
    /// is a build-time authoring gap, never a runtime data condition an authority check should quietly allow or
    /// quietly deny.</summary>
    /// <param name="mutation">The mutation whose kind to look up.</param>
    /// <returns>The declared ordinal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The mutation's runtime type carries no catalog entry.</exception>
    public static int OrdinalOf(WorldMutation mutation) {
        ArgumentNullException.ThrowIfNull(argument: mutation);

        var type = mutation.GetType();

        foreach (var entry in All()) {
            if (entry.Type == type) {
                return entry.Ordinal;
            }
        }

        throw new InvalidOperationException(message: $"WorldMutationKindCatalog: '{type.Name}' carries no catalog entry — every WorldMutation kind must declare a [MutationKind] ordinal.");
    }

    /// <summary>Forces discovery and validation NOW, so a boot sequence that never happens to read
    /// <see cref="All"/> still fails loudly on a broken catalog before any session starts. Idempotent and cheap on a
    /// second call (the cached set is reused).</summary>
    /// <exception cref="InvalidOperationException">The catalog is malformed — see the message for which rule failed.</exception>
    public static void Validate() {
        _ = All();
    }

    private static IReadOnlyList<WorldMutationKindCatalogEntry> Discover() {
        var entries = new List<WorldMutationKindCatalogEntry>();
        var seenOrdinals = new Dictionary<int, Type>();
        var kindTypes = typeof(WorldMutation).GetNestedTypes(bindingAttr: (BindingFlags.Public | BindingFlags.NonPublic))
            .Where(predicate: static type => (type.IsSealed && typeof(WorldMutation).IsAssignableFrom(c: type)))
            .OrderBy(keySelector: static type => type.Name, comparer: StringComparer.Ordinal)
            .ToArray();
        var missing = new List<string>();

        foreach (var type in kindTypes) {
            var attribute = type.GetCustomAttribute<MutationKindAttribute>();

            if (attribute is null) {
                missing.Add(item: type.Name);

                continue;
            }

            if ((attribute.Ordinal < 0) || (attribute.Ordinal > MaxOrdinal)) {
                throw new InvalidOperationException(message: $"WorldMutationKindCatalog: '{type.Name}' declares ordinal {attribute.Ordinal}, outside 0..{MaxOrdinal} — an out-of-lane (or negative) kind would alias an existing bit under the mask's shift and must never be admitted.");
            }

            if (seenOrdinals.TryGetValue(key: attribute.Ordinal, value: out var collidingType)) {
                throw new InvalidOperationException(message: $"WorldMutationKindCatalog: ordinal {attribute.Ordinal} is declared by both '{collidingType.Name}' and '{type.Name}' — every kind must have a UNIQUE explicit ordinal.");
            }

            seenOrdinals.Add(key: attribute.Ordinal, value: type);
            entries.Add(item: new WorldMutationKindCatalogEntry(Type: type, Ordinal: attribute.Ordinal, Section: attribute.Section));
        }

        if (missing.Count > 0) {
            throw new InvalidOperationException(message: $"WorldMutationKindCatalog: {missing.Count} WorldMutation kind(s) carry no [MutationKind] attribute: {string.Join(separator: ", ", values: missing)} — every nested WorldMutation record must declare one.");
        }

        entries.Sort(comparison: static (left, right) => left.Ordinal.CompareTo(value: right.Ordinal));

        return entries;
    }
}
