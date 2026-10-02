using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    /// <summary>The total order grant subjects sort by: kind first (<see cref="GrantSubjectKind"/>'s own declaration
    /// order), then value, then id (the string-keyed kinds' only, State and Region, compared ordinally). Two subjects the
    /// table already treats as equal (record-struct equality) compare equal here too. The order exists to be reproducible
    /// across a rebuild and in canonical snapshots, such as a population checkpoint's revoked admission keys, not to rank
    /// kinds.</summary>
    /// <param name="a">The first subject.</param>
    /// <param name="b">The second subject.</param>
    /// <returns>A negative number when <paramref name="a"/> sorts first, zero when they compare equal, and a positive
    /// number when <paramref name="b"/> sorts first.</returns>
    public static int CompareSubjects(GrantSubject a, GrantSubject b) {
        var kind = ((byte)a.Kind).CompareTo(value: ((byte)b.Kind));

        if (kind != 0) {
            return kind;
        }

        var value = a.Value.CompareTo(value: b.Value);

        return ((value != 0)
            ? value
            : string.CompareOrdinal(
                strA: a.Id,
                strB: b.Id
            )
        );
    }
}
