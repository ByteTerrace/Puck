using System.Reflection;
using Puck.World.Addons;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Discovers every declared refusal in the running build by reflecting over enum types for
/// <see cref="RefusalAttribute"/>-tagged members — see that attribute's remarks for why this scan, not a hand-kept
/// list, is the source of truth <c>world.refusals</c> reads. Built once, lazily, and cached: this is a diagnostic,
/// on-demand read-back (<c>world.refusals</c> is Immediate and reads no simulation state), never a per-tick cost, and
/// the doors themselves pay nothing for it — a refusal's own throw site names an enum member exactly as it always
/// would; nothing here runs until an operator asks.</summary>
/// <remarks>Refusal-tagged enums sit in every layer that owns a door — the state core's <c>state.rule.compile</c> and
/// <c>state.rule.fire</c> doors in Puck.State, the rule substrate's <c>state.transform</c> door in Puck.State.Rules,
/// the document doors in Puck.World.Schema, the tape in Puck.World.Server, <c>addon.mutate</c> in
/// Puck.World.Addons, and <c>sdf.decode</c> in Puck.World.Client. One registry covers them because there is one
/// <see cref="RefusalAttribute"/>, declared in Puck.State, the lowest assembly every one of those projects already
/// references. The list below anchors on one known type per declaring assembly rather than scanning the AppDomain, so
/// it names exactly what it covers. <c>RefusalCatalogAnchorLawTests</c> holds it to the projects whose sources tag a
/// refusal, so an assembly left off it, or one anchored that declares none, fails a law rather than going silently
/// uncataloged.</remarks>
public static class RefusalCatalog {
    private static readonly Assembly[] Assemblies = [
        typeof(CellValue).Assembly,
        typeof(State.Rules.TransformRefusal).Assembly,
        typeof(WorldDefinition).Assembly,
        typeof(WorldServer).Assembly,
        typeof(AddonMutateRefusal).Assembly,
        typeof(Client.Sdf.SdfRefusal).Assembly,
    ];

    private static IReadOnlyList<RefusalCatalogEntry>? Entries;

    /// <summary>Gets the assemblies the catalog scans, one anchored per door-owning project.</summary>
    public static IReadOnlyList<Assembly> AnchoredAssemblies => Assemblies;

    private static IReadOnlyList<RefusalCatalogEntry> Discover() {
        var entries = new List<RefusalCatalogEntry>();
        var seenAssemblies = new HashSet<Assembly>();

        foreach (var assembly in Assemblies) {
            if (!seenAssemblies.Add(item: assembly)) {
                continue;
            }

            foreach (var type in assembly.GetTypes()) {
                if (!type.IsEnum) {
                    continue;
                }

                foreach (var field in type.GetFields(bindingAttr: BindingFlags.Public | BindingFlags.Static)) {
                    if (field.GetCustomAttribute<RefusalAttribute>() is not { } refusal) {
                        continue;
                    }

                    entries.Add(item: new RefusalCatalogEntry(
                        Door: refusal.Door,
                        Id: field.Name,
                        Kind: refusal.Kind,
                        Condition: refusal.Condition,
                        Unsupported: refusal.Unsupported
                    ));
                }
            }
        }

        entries.Sort(comparison: static (left, right) => {
            var byDoor = string.CompareOrdinal(
                strA: left.Door,
                strB: right.Door
            );

            return ((byDoor != 0)
                ? byDoor
                : string.CompareOrdinal(
                    strA: left.Id,
                    strB: right.Id
                )
            );
        });

        return entries;
    }

    /// <summary>Every declared refusal across every door in this build, sorted by door then by id. Computed once and
    /// cached — safe to call repeatedly (e.g. once per <c>world.refusals</c> invocation) at no repeated cost.</summary>
    /// <returns>The catalog.</returns>
    public static IReadOnlyList<RefusalCatalogEntry> All() {
        return (Entries ??= Discover());
    }
}
