using Puck.Commands;

namespace Puck.World;

/// <summary>The sole grammar for render ceilings, tier floors, pins, automatic mode and echoes.</summary>
public readonly record struct WorldRenderScaleCommand(string View, WorldRenderScaleOperation Operation, float Scale) {
    /// <summary>Parses an optional leading view target followed by one operation, refusing surplus arguments.</summary>
    public static bool TryParse(in WireArgs args, out WorldRenderScaleCommand command, out string? refusal) {
        var view = "*";
        var start = 0;

        if ((args.Count > 0) && !IsOperation(token: args[0]) && !TryScale(args[0], out _)) {
            view = args[0].ToString();
            start = 1;
        }
        var count = (args.Count - start);

        command = new(Operation: WorldRenderScaleOperation.Echo, Scale: 0f, View: view);
        refusal = null;
        if (count == 0) {
            return true;
        }
        var token = args[start];

        if (token.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "auto")) {
            if ((count == 1) || ((count == 2) && args.Is(index: (start + 1), value: "on"))) {
                command = command with { Operation = WorldRenderScaleOperation.Auto };
                return true;
            }
            if ((count == 2) && args.Is(index: (start + 1), value: "off")) {
                command = command with { Operation = WorldRenderScaleOperation.Off };
                return true;
            }
        } else if (token.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "floor")) {
            if ((count == 2) && WorldRenderScaleTiers.TryParse(name: args[(start + 1)].ToString(), tier: out var tier)) {
                command = command with { Operation = WorldRenderScaleOperation.Floor, Scale = WorldRenderScaleTiers.Scale(tier: tier) };
                return true;
            }
        } else if (token.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "pin")) {
            if ((count == 2) && TryScale(args[(start + 1)], out var pin)) {
                command = command with { Operation = WorldRenderScaleOperation.Pin, Scale = pin };
                return true;
            }
            refusal = $"world.render-scale: invalid pin '{args.Tail(start: (start + 1))}' for '{view}'";
            return false;
        } else if ((count == 1) && TryScale(scale: out var ceiling, token: token)) {
            command = command with { Operation = WorldRenderScaleOperation.Ceiling, Scale = ceiling };
            return true;
        }
        refusal = $"world.render-scale: expected [view] [<scale>|floor <tier>|pin <scale>|auto [on|off]], got '{args.Tail(start: 0)}'";
        return false;
    }

    private static bool IsOperation(ReadOnlySpan<char> token) => (token.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "floor") ||
        token.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "pin") || token.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "auto"));
    private static bool TryScale(ReadOnlySpan<char> token, out float scale) {
        if (WorldRenderScaleTiers.TryParse(name: token.ToString(), tier: out var tier)) {
            scale = WorldRenderScaleTiers.Scale(tier: tier);
            return true;
        }
        token = token.Trim();
        var percent = (!token.IsEmpty && (token[^1] == '%'));

        if (percent) {
            token = token[..^1];
        }
        if (!CommandArgs.TryParseFloat(text: token, value: out scale)) {
            return false;
        }
        if (percent) {
            scale /= 100f;
        }
        return (float.IsFinite(f: scale) && (scale >= 0.125f) && (scale <= 1f));
    }
}
