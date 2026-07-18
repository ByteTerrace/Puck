using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// One viewport: a content source and the normalized screen region it fills. The region is authored as
/// <c>[x, y, w, h]</c> in 0..1 screen space. The compositor supports up to five viewports.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Viewport {
    /// <summary>The viewport's camera, machine, or capture content source.</summary>
    public ViewportSource? Source { get; init; }
    /// <summary>The normalized screen region <c>[x, y, w, h]</c> in 0..1.</summary>
    public IReadOnlyList<float> Region { get; init; } = [];

    internal void Validate(string path, ValidationErrors errors) {
        if (Source is null) {
            errors.Add(path: $"{path}.source", message: "a viewport requires a source");
        } else {
            Source.Validate(errors: errors, path: $"{path}.source");
        }

        if (!JsonVector.IsValid(components: Region, length: 4)) {
            errors.RequireVector(path: $"{path}.region", components: Region, length: 4);

            return;
        }

        var x = Region[0];
        var y = Region[1];
        var width = Region[2];
        var height = Region[3];

        if ((x < 0f) || (y < 0f) || (width <= 0f) || (height <= 0f) || ((x + width) > 1f) || ((y + height) > 1f)) {
            errors.Add(path: $"{path}.region", message: $"region [{x}, {y}, {width}, {height}] must lie within the unit square (x>=0, y>=0, w>0, h>0, x+w<=1, y+h<=1)");
        }
    }
}
