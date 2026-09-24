using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Decompiler;

public static partial class WorldDecompiler {
    // The members a decompiled source spells an enum-drawn value by. They exist only when the source prints the
    // section's enums as `enum` declarations, which bind every member as a constant; a member is printed bare unless
    // another enum declares it too, since a bare member two enums share names no one ordinal.
    private sealed class EnumSpellings {
        private readonly Dictionary<string, string[]> m_members = new(comparer: StringComparer.Ordinal);
        private readonly HashSet<string> m_shared = new(comparer: StringComparer.Ordinal);

        private EnumSpellings(JsonArray enums) {
            var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var declaration in enums.OfType<JsonObject>()) {
                var members = ((declaration["members"] as JsonArray) ?? []).Select(selector: static member => member!.GetValue<string>()).ToArray();

                m_members[declaration["name"]!.GetValue<string>()] = members;

                foreach (var member in members) {
                    if (!seen.Add(item: member)) {
                        _ = m_shared.Add(item: member);
                    }
                }
            }
        }

        public static EnumSpellings? Of(JsonObject state) => (((state["enums"] is JsonArray enums) && EnumsHaveSugar(enums: enums, records: (state["records"] as JsonArray), world: (state["world"] as JsonArray)))
            ? new EnumSpellings(enums: enums)
            : null
        );
        // The member an Int value of `enumName` spells, or null when the value names no member of it.
        public string? Spell(string? enumName, JsonNode? value) {
            if (
                (enumName is null) ||
                !m_members.TryGetValue(key: enumName, value: out var members) ||
                !DocumentNumbers.TryInteger(node: value, number: out var ordinal) ||
                (((ulong)ordinal) >= ((ulong)members.Length)) ||
                !IdentifierSpelling.IsName(text: members[ordinal]) ||
                (members[ordinal] is "true" or "false" or "null")
            ) {
                return null;
            }

            var member = members[ordinal];

            return (m_shared.Contains(item: member)
                ? $"{enumName}.{member}"
                : member
            );
        }
    }
}
