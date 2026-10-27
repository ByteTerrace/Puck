using Puck.SignedDistance;

namespace Puck.World;

/// <summary>The text-screen template grammar and bounded grid policy, shared by loading and baking.</summary>
public static class WorldScreenText {
    /// <summary>Parses brace escapes and state-cell placeholders. Screens have no per-body binding context.</summary>
    /// <param name="line">The authored line.</param>
    /// <param name="segments">Unescaped literals and state placeholders.</param>
    /// <param name="error">The malformed brace or unsupported token, or empty on success.</param>
    /// <returns>Whether every segment follows the screen grammar.</returns>
    public static bool TryParse(string line, out IReadOnlyList<HudTemplateSegment> segments, out string error) {
        if (!HudTemplate.TryParse(error: out error, segments: out segments, template: line)) {
            return false;
        }

        foreach (var segment in segments) {
            if (segment.IsPlaceholder &&
                (!StateBinding.TryParse(token: segment.Text, binding: out var binding) ||
                (binding.Key == StateBinding.BodyKey) ||
                !CellName.TryParse(candidate: binding.Row, name: out _, reason: out _) ||
                !CellName.TryParse(candidate: (binding.Key ?? WorldStateRow.SlotKey.Value), name: out _, reason: out _))) {
                error = $"placeholder '{{{segment.Text}}}' must name state.<row>[.<key>][.$target] without $body";

                return false;
            }
        }

        return true;
    }
    /// <summary>Returns the grid dimensions. Inferred columns fit the displayed width up to the cell budget;
    /// dynamic expansions beyond those columns are clipped by the baker. Literal text must fit at load.</summary>
    /// <param name="text">The text source.</param>
    /// <param name="width">The widest displayed line, counting Unicode scalars rather than template syntax.</param>
    /// <returns>The column and row counts, each at least one.</returns>
    public static (int Columns, int Rows) Grid(WorldScreenSource.Text text, int width) {
        var rows = Math.Max(val1: (text.Rows ?? text.Lines.Count), val2: 1);
        var columns = Math.Max(val1: (text.Columns ?? Math.Min(val1: Math.Max(val1: width, val2: 1),
            val2: Math.Max(val1: (SdfScreenDecalLayout.MaxScreenDecalCells / rows), val2: 1))), val2: 1);

        return (columns, rows);
    }
}
