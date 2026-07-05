using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The optional input section: how physical controllers route to the run's input consumers (today, the gaming-brick
/// viewport panes). <c>auto</c> adapts to what is connected — one controller multicasts to every brick, two or more
/// map one-to-one by player index; <c>multicast</c> and <c>per-player</c> pin one behavior regardless of controller
/// count. Data only — the demo's pad-routing service consumes it.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InputDocument {
    /// <summary>The recognized routing policy names.</summary>
    public static IReadOnlyList<string> Routings { get; } = ["auto", "multicast", "per-player"];

    /// <summary>The controller→brick routing policy: <c>auto</c> (default), <c>multicast</c>, or <c>per-player</c>.</summary>
    public string GamepadRouting { get; init; } = "auto";

    internal void Validate(string path, ValidationErrors errors) {
        if (!Routings.Contains(value: GamepadRouting, comparer: StringComparer.OrdinalIgnoreCase)) {
            errors.Add(path: $"{path}.gamepadRouting", message: $"'{GamepadRouting}' is not a routing policy; expected one of: {string.Join(separator: ", ", values: Routings)}");
        }
    }
}
