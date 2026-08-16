using System.Reflection;

namespace Puck.World.Protocol;

/// <summary>Declares one <see cref="WorldMutation"/> nested record's stable dispatch ordinal and the
/// <see cref="WorldSection"/> it targets — the wire vocabulary <see cref="MutationKindMask"/> and the addon mutation
/// door (<c>Server.WorldAddonMutationDecoder</c>) read off. Every nested record under <see cref="WorldMutation"/>
/// carries exactly one of these, with an explicit ordinal (never inferred from declaration order — a reordered file
/// must never silently renumber the wire). <see cref="WorldMutationKindCatalog"/> discovers every so-tagged record by
/// reflection over this assembly and validates the whole set at boot, the same discovered-not-hand-kept posture
/// <c>RefusalAttribute</c> uses for <c>world.refusals</c>.</summary>
/// <param name="ordinal">The kind's stable dispatch ordinal, <c>0..</c><see cref="WorldMutationKindCatalog.MaxOrdinal"/>
/// (one bit of the <see cref="MutationKindMask"/> lane). An ordinal past the lane is refused at boot rather than left
/// to wrap: .NET masks a shift count by the operand's width, so an out-of-lane bit aliases a real kind and would
/// admit the wrong door silently.</param>
/// <param name="section">The <see cref="WorldSection"/> this kind targets (must equal what
/// <c>Server.WorldServer.SectionOf</c> maps the same record type to — that cross-check is Server-side, since this
/// project cannot reference Server; this attribute only records the declared pairing).</param>
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
/// missing the attribute. A violation fails boot loudly (a <see cref="InvalidOperationException"/> thrown from
/// <see cref="Validate"/>) — an ordinal collision or an out-of-range value is a build-time authoring defect, never a
/// runtime data condition, so it must never reach a live session. This is the catalog only: it does not build a
/// <see cref="MutationKindMask"/> or the addon door's guest-reachable ordinal subset — see <see cref="KindsOf"/> for
/// the mask projection and <c>Server.WorldAddonMutationDecoder</c> for which ordinals a guest may actually dispatch.
/// </summary>
public static class WorldMutationKindCatalog {
    /// <summary>The highest ordinal a kind may declare — the top bit of the 128-bit
    /// <see cref="MutationKindMask"/> lane these pack into. An ordinal past the lane must be refused here rather
    /// than left to wrap: .NET masks a shift count by the operand's width, so a shift at or beyond the lane's own
    /// width does not overflow to zero as intended but wraps around, silently admitting an out-of-range ordinal
    /// under some other kind's bit — a grant quietly opening the wrong door rather than failing. The ceiling must
    /// track the lane's own width if it ever changes.</summary>
    public const int MaxOrdinal = 127;

    private static readonly MutationKindMask[] KindsBySection = new MutationKindMask[(Enum.GetValues<WorldSection>().Length)];

    private static IReadOnlyList<WorldMutationKindCatalogEntry>? Entries;
    private static bool KindsBySectionBuilt;

    private static IReadOnlyList<WorldMutationKindCatalogEntry> Discover() {
        var entries = new List<WorldMutationKindCatalogEntry>();
        var seenOrdinals = new Dictionary<int, Type>();
        var kindTypes = typeof(WorldMutation).GetNestedTypes(bindingAttr: BindingFlags.Public | BindingFlags.NonPublic)
            .Where(predicate: static type => (type.IsSealed && typeof(WorldMutation).IsAssignableFrom(c: type)))
            .OrderBy(
            keySelector: static type => type.Name,
            comparer: StringComparer.Ordinal
        )
            .ToArray();
        var missing = new List<string>();

        foreach (var type in kindTypes) {
            var attribute = type.GetCustomAttribute<MutationKindAttribute>();

            if (attribute is null) {
                missing.Add(item: type.Name);

                continue;
            }

            if (
                (attribute.Ordinal < 0) ||
                (attribute.Ordinal > MaxOrdinal)
            ) {
                throw new InvalidOperationException(message: $"WorldMutationKindCatalog: '{type.Name}' declares ordinal {attribute.Ordinal}, outside 0..{MaxOrdinal} — an out-of-lane (or negative) kind would alias an existing bit under the mask's shift and must never be admitted.");
            }

            if (seenOrdinals.TryGetValue(
                key: attribute.Ordinal,
                value: out var collidingType
            )) {
                throw new InvalidOperationException(message: $"WorldMutationKindCatalog: ordinal {attribute.Ordinal} is declared by both '{collidingType.Name}' and '{type.Name}' — every kind must have a UNIQUE explicit ordinal.");
            }

            seenOrdinals.Add(
                key: attribute.Ordinal,
                value: type
            );
            entries.Add(item: new WorldMutationKindCatalogEntry(
                Type: type,
                Ordinal: attribute.Ordinal,
                Section: attribute.Section
            ));
        }

        if (missing.Count > 0) {
            throw new InvalidOperationException(message: $"WorldMutationKindCatalog: {missing.Count} WorldMutation kind(s) carry no [MutationKind] attribute: {string.Join(
                separator: ", ",
                values: missing
            )} — every nested WorldMutation record must declare one.");
        }

        entries.Sort(comparison: static (left, right) => left.Ordinal.CompareTo(value: right.Ordinal));

        return entries;
    }

    /// <summary>Returns every cataloged mutation kind, sorted by ordinal. Discovered once, cached, and validated on first
    /// access (see <see cref="Validate"/> for what a violation does).</summary>
    /// <returns>The catalog.</returns>
    public static IReadOnlyList<WorldMutationKindCatalogEntry> All() {
        return (Entries ??= Discover());
    }
    /// <summary>Describes a mask's admitted kinds by declared record name, comma-separated. The
    /// <see cref="MutationKindVocabularyHook.Describe"/> implementation the composition root installs — see that
    /// hook's remarks for why <see cref="MutationKindMask"/> itself cannot call <see cref="All"/> directly.</summary>
    /// <param name="mask">The mask to describe.</param>
    /// <returns>The comma-separated kind names, or <c>&lt;none&gt;</c> when the mask admits nothing.</returns>
    public static string DescribeMask(MutationKindMask mask) {
        var builder = new System.Text.StringBuilder();

        foreach (var entry in All()) {
            if (!mask.Contains(ordinal: entry.Ordinal)) {
                continue;
            }

            _ = builder.Append(value: ((builder.Length == 0)
                ? string.Empty
                : ",")).Append(value: entry.Type.Name);
        }

        return ((builder.Length == 0)
            ? "<none>"
            : builder.ToString()
        );
    }
    /// <summary>Returns the mask of every kind ordinal declared under <paramref name="section"/> — the ceiling a
    /// <see cref="WorldGrant.KindMask"/> row over that section's subject may legitimately name (see
    /// <c>Server.WorldGrants</c>'s grant-door remarks for why a bit outside this set is refused rather than
    /// silently admitted-and-inert). Built once, from the validated catalog, and cached.</summary>
    /// <param name="section">The section to project.</param>
    /// <returns>The section's kind mask.</returns>
    public static MutationKindMask KindsOf(WorldSection section) {
        if (!KindsBySectionBuilt) {
            foreach (var entry in All()) {
                KindsBySection[((int)entry.Section)] = KindsBySection[((int)entry.Section)].With(ordinal: entry.Ordinal);
            }

            KindsBySectionBuilt = true;
        }

        return KindsBySection[((int)section)];
    }
    /// <summary>Returns the declared dispatch ordinal of one live mutation's own kind — the bit a
    /// <see cref="MutationKindMask"/> must carry for a grant row to admit it. Throws rather than returning a
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
    /// <summary>Parses the comma-separated kind-name form <see cref="DescribeMask"/> writes. The
    /// <see cref="MutationKindVocabularyHook.TryParse"/> implementation the composition root installs.</summary>
    /// <param name="text">The comma-separated kind names.</param>
    /// <param name="mask">The parsed mask, on success.</param>
    /// <param name="unknown">The first unrecognized name, on failure.</param>
    /// <returns><see langword="true"/> when every name resolved.</returns>
    public static bool TryParseMask(string? text, out MutationKindMask mask, out string unknown) {
        mask = MutationKindMask.Empty;
        unknown = string.Empty;

        if (string.IsNullOrEmpty(value: text)) {
            return false;
        }

        foreach (var name in text.Split(
            options: StringSplitOptions.None,
            separator: ','
        )) {
            var matched = false;

            foreach (var entry in All()) {
                if (string.Equals(
                    a: entry.Type.Name,
                    b: name,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    mask = mask.With(ordinal: entry.Ordinal);
                    matched = true;

                    break;
                }
            }

            if (!matched) {
                unknown = name;

                return false;
            }
        }

        return true;
    }
    /// <summary>Forces discovery and validation now, so a boot sequence that never happens to read
    /// <see cref="All"/> still fails loudly on a broken catalog before any session starts. Idempotent and cheap on a
    /// second call (the cached set is reused).</summary>
    /// <exception cref="InvalidOperationException">The catalog is malformed — see the message for which rule failed.</exception>
    public static void Validate() {
        _ = All();
    }
}
