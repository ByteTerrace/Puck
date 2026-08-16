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
/// <remarks>Scans four assemblies, not one: the split between <c>Puck.World.Schema</c>, <c>Puck.World.Server</c>,
/// <c>Puck.World.Addons</c>, and this composition root put refusal-tagged enums in Puck.World.Schema
/// (e.g. <c>WorldGrant</c>'s), Puck.World.Server (<c>WorldReplayRefusal</c>), Puck.World.Addons
/// (<c>AddonMutateRefusal</c>), and this composition root (<c>Client.Sdf.SdfRefusal</c>). A single-assembly scan
/// would silently stop seeing three quarters of the catalog; the assembly list below is anchored on one known type
/// per assembly rather than an AppDomain-wide scan, so it names exactly what it means to cover.</remarks>
internal static class RefusalCatalog {
    private static readonly Assembly[] Assemblies = [
        typeof(RefusalCatalog).Assembly,
        typeof(RefusalAttribute).Assembly,
        typeof(WorldServer).Assembly,
        typeof(AddonMutateRefusal).Assembly,
    ];

    private static IReadOnlyList<RefusalCatalogEntry>? Entries;

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
                        Condition: refusal.Condition
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
