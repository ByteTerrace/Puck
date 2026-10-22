using System.Globalization;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.World.Client;

namespace Puck.World;

/// <summary>One viewport of the active layout a capture writes as its own PNG: the slot's region of the composed frame,
/// in fractions of it with the origin top-left, and the name its file carries.</summary>
/// <param name="Name">The file-safe name: the slot's camera, its <c>views.graphs</c> instance, or <c>seat&lt;n&gt;</c>.</param>
/// <param name="Region">The slot's region of the composed frame.</param>
internal sealed record WorldSlotStill(string Name, NormalizedRect Region);

/// <summary>
/// Writes each viewport of a multi-slot layout as its own PNG beside a settled capture (<c>world.screenshot &lt;path.png&gt;
/// slots</c>): the composed frame cut at each slot's region, named <c>&lt;stem&gt;-&lt;camera&gt;.png</c>. The cut is of the
/// frame the capture served, so every slot shows the same tick the capture's <c>[still]</c> line names.
/// </summary>
internal static class WorldSlotStills {
    /// <summary>Returns the stills the active layout's slots give, named by what each slot shows; a name two slots share
    /// is numbered by slot.</summary>
    /// <param name="slots">The composer's current slots.</param>
    /// <returns>One still per slot with a visible region, in slot order.</returns>
    public static IReadOnlyList<WorldSlotStill> Of(IReadOnlyList<WorldComposedSlot> slots) {
        ArgumentNullException.ThrowIfNull(argument: slots);

        var stills = new List<WorldSlotStill>(capacity: slots.Count);

        for (var index = 0; (index < slots.Count); index++) {
            var slot = slots[index];

            if ((slot.Region.Width <= 0f) || (slot.Region.Height <= 0f)) {
                continue;
            }

            var name = SafeName(name: (slot.Camera ?? slot.Instance ?? string.Create(provider: CultureInfo.InvariantCulture, handler: $"seat{slot.SeatOrder}")));

            if (slots.Where(predicate: other => string.Equals(a: SafeName(name: (other.Camera ?? other.Instance ?? string.Create(provider: CultureInfo.InvariantCulture, handler: $"seat{other.SeatOrder}"))), b: name, comparisonType: StringComparison.Ordinal)).Skip(count: 1).Any()) {
                name = string.Create(provider: CultureInfo.InvariantCulture, handler: $"{name}-{index}");
            }

            stills.Add(item: new WorldSlotStill(Name: name, Region: slot.Region));
        }

        return stills;
    }
    /// <summary>Cuts a served settled capture into its slot stills and narrates each file on stderr, or the reason none
    /// was written.</summary>
    /// <param name="result">The capture's terminal result.</param>
    /// <param name="slots">The stills to cut, from <see cref="Of"/> at arming.</param>
    public static void Write(FrameCaptureResult result, IReadOnlyList<WorldSlotStill> slots) {
        ArgumentNullException.ThrowIfNull(argument: result);
        ArgumentNullException.ThrowIfNull(argument: slots);

        if (!result.Succeeded) {
            return;
        }

        try {
            var image = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: result.Path));
            var stem = Path.Combine(
                path1: (Path.GetDirectoryName(path: result.Path) ?? string.Empty),
                path2: Path.GetFileNameWithoutExtension(path: result.Path)
            );

            foreach (var slot in slots) {
                var left = Edge(fraction: slot.Region.X, extent: image.Width);
                var top = Edge(fraction: slot.Region.Y, extent: image.Height);
                var width = (Edge(fraction: (slot.Region.X + slot.Region.Width), extent: image.Width) - left);
                var height = (Edge(fraction: (slot.Region.Y + slot.Region.Height), extent: image.Height) - top);

                if ((width <= 0) || (height <= 0)) {
                    Console.Error.WriteLine(value: $"[still] slot {slot.Name}: its region lies outside the {image.Width}x{image.Height} frame — no file written");

                    continue;
                }

                var pixels = new byte[((width * height) * 4)];

                for (var row = 0; (row < height); row++) {
                    image.RgbaPixels.AsSpan(start: ((((top + row) * image.Width) + left) * 4), length: (width * 4)).CopyTo(destination: pixels.AsSpan(start: ((row * width) * 4)));
                }

                var path = $"{stem}-{slot.Name}.png";

                PngEncoder.Write(height: height, path: path, rgba: pixels, width: width);
                Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[still] slot {slot.Name} {width}x{height} at {left},{top} -> {path}"));
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)) {
            Console.Error.WriteLine(value: $"[still] slots of {result.Path} were not written ({exception.Message})");
        }
    }

    // A slot edge in whole pixels, clamped to the frame.
    private static int Edge(float fraction, int extent) => Math.Clamp(
        max: extent,
        min: 0,
        value: ((int)MathF.Round(x: (fraction * extent)))
    );
    // The name with every character a file name cannot carry replaced by '-'.
    private static string SafeName(string name) {
        var invalid = Path.GetInvalidFileNameChars();

        return string.Create(length: name.Length, state: (name, invalid), action: static (span, state) => {
            for (var index = 0; (index < span.Length); index++) {
                var character = state.name[index];

                span[index] = ((Array.IndexOf(array: state.invalid, value: character) >= 0) || (character == '$')) ? '-' : character;
            }
        });
    }
}
