using System.Text.Json;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World;

namespace Puck.Cli.Parity;

/// <summary>
/// The exact image the parity world's <c>binding</c> station captures at a tick, computed on the CPU from the same
/// integer steps its three passes take (<c>binding-seed.hlsl</c>, <c>binding-pixelate.hlsl</c>,
/// <c>binding-grain.hlsl</c>), so a pass that reads a wrong config value, a wrong binding or a wrong frame value fails
/// the capture by pixel on either backend, not only when the two backends disagree. The config comes from the graph
/// document's defaults and the step rate from the world, never a second statement of either here; the frame group's
/// tick at a captured simulation tick is that tick times the engine ticks of one step, as the offscreen pump advances it.
/// </summary>
internal sealed class ParityBindingReference {
    private const string GrainPass = "grain";
    private const string OutputResource = "image";
    private const string PixelatePass = "pixelate";

    private readonly uint m_amplitude;
    private readonly uint m_cellSize;
    private readonly uint m_flickerHz;
    private readonly uint[] m_levels;
    private readonly uint m_seed;
    private readonly ulong m_stepTicks;

    private ParityBindingReference(uint width, uint height, uint cellSize, uint[] levels, uint seed, uint amplitude, uint flickerHz, ulong stepTicks) {
        Width = width;
        Height = height;
        m_amplitude = amplitude;
        m_cellSize = cellSize;
        m_flickerHz = flickerHz;
        m_levels = levels;
        m_seed = seed;
        m_stepTicks = stepTicks;
    }

    /// <summary>Gets the image's width in pixels.</summary>
    public uint Width { get; }
    /// <summary>Gets the image's height in pixels.</summary>
    public uint Height { get; }

    private static uint Hash(uint value) {
        unchecked {
            value ^= (value >> 16);
            value *= 0x7FEB352Du;
            value ^= (value >> 15);
            value *= 0x846CA68Bu;
            value ^= (value >> 16);
        }

        return value;
    }
    private static JsonElement Default(RenderGraphDefinition graph, string pass, string field) {
        var declaration = ((graph.Passes ?? []).FirstOrDefault(predicate: candidate => string.Equals(
            a: candidate.Name,
            b: pass,
            comparisonType: StringComparison.Ordinal
        )) ?? throw new InvalidDataException(message: $"the binding graph has no pass '{pass}'."));

        return ((declaration.Config?.TryGetValue(
            key: field,
            value: out var config
        ) == true) && (config.Default is { } value)
            ? value
            : throw new InvalidDataException(message: $"the binding graph's pass '{pass}' has no default for '{field}'."));
    }

    /// <summary>Reads the reference's inputs: the binding graph's config defaults and output extent, and the world's
    /// simulation rate.</summary>
    /// <param name="graphPath">The binding graph document.</param>
    /// <param name="worldPath">The world document that runs it.</param>
    /// <param name="reference">The reference, when this returns <see langword="true"/>.</param>
    /// <param name="error">Why the inputs were refused, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when both documents supply every input.</returns>
    public static bool TryLoad(string graphPath, string worldPath, out ParityBindingReference reference, out string error) {
        reference = null!;
        error = string.Empty;

        try {
            var graph = ShaderPipelineLoader.ReadDefinition(
                name: "binding",
                path: graphPath
            );
            var output = (graph.Resources.FirstOrDefault(predicate: static resource => string.Equals(
                a: resource.Name,
                b: OutputResource,
                comparisonType: StringComparison.Ordinal
            )) ?? throw new InvalidDataException(message: $"the binding graph has no resource '{OutputResource}'."));
            var dimensions = output.Dimensions;

            if (
                (dimensions is null) ||
                (dimensions.Mode != ShaderPipelineDimensionMode.Absolute) ||
                (dimensions.Width is not (>= 1 and <= 4096)) ||
                (dimensions.Height is not (>= 1 and <= 4096)) ||
                (dimensions.Width != Math.Floor(d: dimensions.Width)) ||
                (dimensions.Height != Math.Floor(d: dimensions.Height))
            ) {
                throw new InvalidDataException(message: $"the binding graph's '{OutputResource}' must have a whole absolute extent.");
            }

            var width = ((uint)dimensions.Width);
            var height = ((uint)dimensions.Height);

            var rateHz = WorldDefinitionSerialization.Deserialize(
                documentDirectory: Path.GetDirectoryName(path: worldPath),
                utf8Json: File.ReadAllBytes(path: worldPath)
            ).SimulationRateHz;

            if (
                (rateHz <= 0) ||
                ((EngineTicks.PerSecond % ((ulong)rateHz)) != 0UL)
            ) {
                throw new InvalidDataException(message: $"the world's simulation rate {rateHz} Hz does not step a whole number of engine ticks.");
            }

            reference = new ParityBindingReference(
                amplitude: Default(graph: graph, field: "amplitude", pass: GrainPass).GetUInt32(),
                cellSize: Default(graph: graph, field: "cellSize", pass: PixelatePass).GetUInt32(),
                flickerHz: Default(graph: graph, field: "flickerHz", pass: GrainPass).GetUInt32(),
                height: height,
                levels: [.. Default(graph: graph, field: "levels", pass: PixelatePass).EnumerateArray().Select(selector: static level => level.GetUInt32())],
                seed: Default(graph: graph, field: "seed", pass: GrainPass).GetUInt32(),
                stepTicks: (EngineTicks.PerSecond / ((ulong)rateHz)),
                width: width
            );

            if (reference.m_levels.Length != 3) {
                throw new InvalidDataException(message: "the binding graph's levels default must hold three counts.");
            }

            return true;
        } catch (Exception exception) when ((exception is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)) {
            reference = null!;
            error = $"the binding reference could not be read from '{graphPath}' and '{worldPath}': {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }

    /// <summary>Writes the image the station captures at a simulation tick, as tightly packed RGBA8, red first, row by
    /// row.</summary>
    /// <param name="tick">The captured simulation tick.</param>
    /// <returns>The image.</returns>
    public byte[] Render(ulong tick) {
        var width = Width;
        var height = Height;
        var image = new byte[checked(((int)width * (int)height) * 4)];
        var cell = Math.Max(1U, m_cellSize);
        var tickRate = ((uint)EngineTicks.PerSecond);
        var grainFrame = unchecked(((uint)(tick * m_stepTicks)) / Math.Max(1U, (tickRate / Math.Max(1U, m_flickerHz))));
        var grainKey = Hash(value: unchecked(m_seed ^ (grainFrame * 0x9E3779B9u)));

        uint Seed(uint x, uint y, int channel) => channel switch {
            0 => (((x * 37U) + (y * 11U)) & 255U),
            1 => (((x ^ y) * 5U) & 255U),
            _ => ((y * 255U) / Math.Max(1U, (height - 1U))),
        };

        for (var y = 0U; (y < height); y++) {
            for (var x = 0U; (x < width); x++) {
                var centreX = Math.Min((((x / cell) * cell) + (cell / 2U)), (width - 1U));
                var centreY = Math.Min((((y / cell) * cell) + (cell / 2U)), (height - 1U));
                var noise = Hash(value: unchecked((x + (y * 4099U)) ^ grainKey));
                var offset = (((int)(noise % ((2U * m_amplitude) + 1U))) - ((int)m_amplitude));
                var at = checked(((((int)y * (int)width) + (int)x) * 4));

                for (var channel = 0; (channel < 3); channel++) {
                    var steps = (Math.Max(m_levels[channel], 2U) - 1U);
                    var level = (((Seed(channel: channel, x: centreX, y: centreY) * steps) + 127U) / 255U);
                    var code = (((level * 255U) + (steps / 2U)) / steps);

                    image[(at + channel)] = ((byte)Math.Clamp(
                        max: 255,
                        min: 0,
                        value: (((int)code) + offset)
                    ));
                }

                image[(at + 3)] = 255;
            }
        }

        return image;
    }
}
