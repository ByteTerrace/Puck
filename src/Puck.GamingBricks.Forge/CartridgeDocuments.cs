using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;

using Puck.Maths;

namespace Puck.GamingBricks.Forge;

/// <summary>The cartridge document's parse, validate and canonicalize boundary, shared by editors and both compilers.</summary>
public static class CartridgeDocuments {
    /// <summary>The bounded source size accepted by the editor and parser, in UTF-8 bytes.</summary>
    public const int MaximumSourceBytes = 1_048_576;
    private static readonly JsonSerializerOptions s_json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        AllowDuplicateProperties = false,
    };

    /// <summary>Creates an empty, editable cartridge whose defaults are explicit source data.</summary>
    /// <param name="target">cgb or agb.</param>
    /// <param name="title">The player's title.</param>
    /// <returns>A blank document with one empty tile and no game rules.</returns>
    public static CartridgeDocument Create(string target, string title) {
        var palette = new int[target == "agb" ? 16 : 4];
        palette[0] = 0x7FFF;
        palette[1] = 0x56B5;
        palette[2] = 0x294A;
        var palettes = new CartridgePalettes(Background: [palette], Object: [palette]);
        return Canonicalize(document: new CartridgeDocument {
            Schema = CartridgeDocument.SchemaId,
            Target = target,
            Title = title,
            GameCode = "PUCK",
            Palettes = palettes,
            Tiles = [new CartridgeTile(Name: "blank", Pixels: Enumerable.Repeat(element: "00000000", count: 8).ToArray())],
            Map = new int[1024],
            Variables = [],
            Arrays = [],
            Screens = [],
            Sounds = [],
            Layers = [],
            Raster = [],
            Rules = [],
            Sprites = [],
            ScrollX = new CartridgeValue(Constant: 0),
            ScrollY = new CartridgeValue(Constant: 0),
        }).Document;
    }

    /// <summary>Parses strict source JSON and checks all semantic constraints.</summary>
    /// <param name="utf8">The source bytes.</param>
    /// <returns>The validated document.</returns>
    public static CartridgeDocument Parse(ReadOnlySpan<byte> utf8) {
        if (utf8.Length > MaximumSourceBytes) {
            throw new ArgumentException(message: $"Cartridge source exceeds {MaximumSourceBytes} bytes.", paramName: nameof(utf8));
        }
        var document = JsonSerializer.Deserialize<CartridgeDocument>(utf8Json: utf8, options: s_json)
            ?? throw new JsonException(message: "Expected a cartridge document object.");
        return Canonicalize(document: document).Document;
    }

    /// <summary>Validates, normalizes spelling and computes the shared canonical source identity.</summary>
    /// <param name="document">The authored document.</param>
    /// <returns>The canonical source, bytes and hash.</returns>
    public static CanonicalDocument<CartridgeDocument> Canonicalize(CartridgeDocument document) {
        DocumentCanonicalizer.ThrowIfInvalid(errors: Validate(document: document), source: null);
        return DocumentCanonicalizer.Canonicalize(document: document with {
            Title = document.Title.ToUpperInvariant(),
            Tiles = document.Tiles.Select(selector: tile => tile with { Pixels = tile.Pixels.Select(selector: static row => row.ToUpperInvariant()).ToArray() }).ToArray(),
        });
    }

    /// <summary>Collects source-path diagnostics without compiling or running a cartridge.</summary>
    /// <param name="document">The authored source.</param>
    /// <returns>Every detected violation; an empty list means valid.</returns>
    /// <remarks>
    /// Validation refuses what makes an image wrong — a shape the hardware has no room for — never what merely makes
    /// it slow. A cartridge that misses frames still runs, and the machine absorbs that case already, so the per-frame
    /// cost is reported on the compilation rather than refused here.
    /// </remarks>
    public static IReadOnlyList<DocumentValidationError> Validate(CartridgeDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);
        var check = new CartridgeValidation(document: document);
        return check.Run();
    }

    /// <summary>Estimates a document's per-frame work and the reservation its target grants.</summary>
    /// <param name="document">The authored source.</param>
    /// <returns>The estimate, and the target's reservation for comparison.</returns>
    /// <remarks>Advice for an author, not a gate: a document over its reservation compiles and runs, more slowly.</remarks>
    public static (CostBound Frame, long Reservation) Estimate(CartridgeDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var profile = document.Target is "agb" or "cgb" ? CartridgeCostProfile.For(target: document.Target) : CartridgeCostProfile.Humble;
        return (CartridgeCost.Frame(document: document, profile: profile), profile.FrameUnits);
    }
}
