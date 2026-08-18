using System.Collections;
using System.Reflection;

using Puck.World.Protocol;

namespace Puck.World.Tests;

// A structural deep comparer over a checkpoint record graph: default record equality compares an array/list field
// by REFERENCE, not by content, so two independently captured checkpoints with byte-identical arrays would read as
// unequal under a naive Equals() call. This walks public instance properties recursively, treating any
// non-string IEnumerable as an ordered element-wise sequence, and falls back to Equals() only at a genuine leaf
// (primitives, strings, enums, and value types with no further public properties to walk).
internal static class DeepEqual {
    /// <summary>The property path <see cref="Compare"/> last found a mismatch at — diagnostic only, read after a
    /// failed comparison to name where the two graphs diverged.</summary>
    public static string LastMismatchPath { get; private set; } = string.Empty;

    public static bool Compare(object? a, object? b, string path = "$") {
        if (ReferenceEquals(objA: a, objB: b)) {
            return true;
        }
        if (
            (a is null) ||
            (b is null)
        ) {
            LastMismatchPath = path;

            return false;
        }

        var type = a.GetType();

        // PlayerIntent's own explicit element-wise Equals is the one door onto its InlineArray backing store — the
        // compiler's default struct equality throws for an InlineArray, and there is no public indexer to walk it
        // any other way from outside the type.
        if (
            (a is PlayerIntent intentA) &&
            (b is PlayerIntent intentB)
        ) {
            var equal = intentA.Equals(other: intentB);

            if (!equal) {
                LastMismatchPath = path;
            }

            return equal;
        }
        // Sequence content, not concrete collection type: a collection-expression spread (`[.. source]`) and a
        // plain array holding the identical elements are two different runtime types that must still compare equal
        // here — this check runs BEFORE the exact-type gate below on purpose, for every non-string sequence.
        if (
            (a is not string) &&
            (a is IEnumerable enumerableA) &&
            (b is IEnumerable enumerableB)
        ) {
            var itemsA = enumerableA.Cast<object?>().ToArray();
            var itemsB = enumerableB.Cast<object?>().ToArray();

            if (itemsA.Length != itemsB.Length) {
                LastMismatchPath = $"{path} (count {itemsA.Length} vs {itemsB.Length})";

                return false;
            }

            for (var index = 0; (index < itemsA.Length); index++) {
                if (!Compare(
                    a: itemsA[index],
                    b: itemsB[index],
                    path: $"{path}[{index}]"
                )) {
                    return false;
                }
            }

            return true;
        }

        if (type != b.GetType()) {
            LastMismatchPath = $"{path} (type {type.Name} vs {b.GetType().Name})";

            return false;
        }
        if (
            type.IsPrimitive ||
            type.IsEnum ||
            (a is string) ||
            (a is decimal)
        ) {
            var equal = a.Equals(obj: b);

            if (!equal) {
                LastMismatchPath = $"{path} ({a} vs {b})";
            }

            return equal;
        }

        var properties = type.GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Instance);

        if (properties.Length == 0) {
            var equal = a.Equals(obj: b);

            if (!equal) {
                LastMismatchPath = path;
            }

            return equal;
        }

        foreach (var property in properties) {
            if (property.GetIndexParameters().Length != 0) {
                continue;
            }

            var valueA = property.GetValue(obj: a);
            var valueB = property.GetValue(obj: b);

            if (!Compare(
                a: valueA,
                b: valueB,
                path: $"{path}.{property.Name}"
            )) {
                return false;
            }
        }

        return true;
    }
}
