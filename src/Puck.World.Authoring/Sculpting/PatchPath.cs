namespace Puck.World.Authoring.Sculpting;

/// <summary>One segment of a <see cref="SculptPatch.SetMember"/>/<see cref="SculptPatch.RemoveMember"/> dotted path: a
/// member name, optionally carrying a <c>[field=value]</c> selector that descends into an array member of this name
/// and finds (or creates) the element whose <see cref="SelectorField"/> equals <see cref="SelectorValue"/>.</summary>
/// <param name="Name">The member name.</param>
/// <param name="SelectorField">The selector's field name, or null for a plain object member.</param>
/// <param name="SelectorValue">The selector's value, or null.</param>
public readonly record struct PatchPathSegment(string Name, string? SelectorField, string? SelectorValue);

/// <summary>Parses and renders the small path grammar <see cref="SculptPatch"/>'s member operations share:
/// <c>segment ("." segment)*</c>, <c>segment := name ("[" field "=" value "]")?</c>.</summary>
public static class PatchPath {
    /// <summary>Parses a dotted path into its segments.</summary>
    /// <param name="path">The path text.</param>
    /// <exception cref="FormatException">A segment's bracket is unterminated or carries no <c>field=value</c>.</exception>
    public static IReadOnlyList<PatchPathSegment> Parse(string path) {
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        var segments = new List<PatchPathSegment>();

        foreach (var raw in path.Split(separator: '.')) {
            var bracket = raw.IndexOf(value: '[');

            if (bracket < 0) {
                segments.Add(item: new PatchPathSegment(
                    Name: raw,
                    SelectorField: null,
                    SelectorValue: null
                ));

                continue;
            }

            if (!raw.EndsWith(value: ']')) {
                throw new FormatException(message: $"malformed path segment '{raw}' — an unterminated selector.");
            }

            var name = raw[..bracket];
            var inner = raw[(bracket + 1)..^1];
            var equals = inner.IndexOf(value: '=');

            if (equals < 0) {
                throw new FormatException(message: $"malformed selector in '{raw}' — expected field=value.");
            }

            segments.Add(item: new PatchPathSegment(
                Name: name,
                SelectorField: inner[..equals],
                SelectorValue: inner[(equals + 1)..]
            ));
        }

        return segments;
    }
    /// <summary>Renders one segment back to its authored spelling.</summary>
    public static string Render(PatchPathSegment segment) => ((segment.SelectorField is { } field)
        ? $"{segment.Name}[{field}={segment.SelectorValue}]"
        : segment.Name);
}
