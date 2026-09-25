using System.Text.Json;

namespace Puck.Cli.Canary;

internal static partial class CanaryManifestLoader {
    // backends belongs to the two GPU shapes. An offscreen proof must name them; a windowed proof may, and then runs each
    // leg once per backend like an offscreen one. Every other shape keeps its one boot per leg. A proof that names
    // backends names every backend a leg boots (WorldOffscreenLeg.Backends), so none can silently go unexercised.
    private static IReadOnlyList<string> ReadBackends(JsonElement element, string id, CanaryBootShape bootShape, IReadOnlyList<string> requirements, CanaryLeg positive, CanaryLeg discriminating) {
        if (!element.TryGetProperty(
            propertyName: "backends",
            value: out var backendsElement
        )) {
            if (bootShape == CanaryBootShape.Offscreen) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' bootShape 'offscreen' requires backends, exactly {string.Join(
                    separator: " and ",
                    values: WorldOffscreenLeg.Backends
                )}.");
            }

            return [];
        }

        if (bootShape is not (CanaryBootShape.Offscreen or CanaryBootShape.Windowed)) {
            throw new CanaryManifestRefusal(message: $"canary '{id}' declares backends, which only bootShape 'offscreen' and 'windowed' read.");
        }
        if (backendsElement.ValueKind != JsonValueKind.Array) {
            throw new CanaryManifestRefusal(message: $"canary '{id}' backends must be an array.");
        }

        var backends = new List<string>(capacity: backendsElement.GetArrayLength());

        foreach (var item in backendsElement.EnumerateArray()) {
            var backend = ((item.ValueKind == JsonValueKind.String)
                ? item.GetString()
                : null);

            if (
                (backend is null) ||
                !WorldOffscreenLeg.Backends.Contains(
                value: backend,
                comparer: StringComparer.Ordinal
            ) ||
                backends.Contains(
                value: backend,
                comparer: StringComparer.Ordinal
            )
            ) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' backends entry '{item}' is invalid; list each of {string.Join(
                    separator: " and ",
                    values: WorldOffscreenLeg.Backends
                )} exactly once.");
            }

            backends.Add(item: backend);
        }

        if (backends.Count != WorldOffscreenLeg.Backends.Count) {
            throw new CanaryManifestRefusal(message: $"canary '{id}' backends must list {string.Join(
                separator: " and ",
                values: WorldOffscreenLeg.Backends
            )}; a proof that skips a backend cannot pass the two-backend gate.");
        }
        if (!requirements.Contains(
            value: "gpu",
            comparer: StringComparer.Ordinal
        )) {
            throw new CanaryManifestRefusal(message: $"canary '{id}' declares backends, so it needs a GPU and must declare the 'gpu' requirement.");
        }
        foreach (var leg in ((CanaryLeg[])[positive, discriminating])) {
            if (
                leg.Connect ||
                (leg.AuthorityWorldPath is not null) ||
                (leg.Authorities.Count != 0)
            ) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' {leg.Name} leg: a proof with backends runs one local process per backend; authorities, authorityWorld, and connect are refused.");
            }
        }

        return backends;
    }
    private static IReadOnlyList<double>? ReadChannelBounds(JsonElement element, string member, string context) {
        if (!element.TryGetProperty(
            propertyName: member,
            value: out var boundsElement
        )) {
            return null;
        }
        if (
            (boundsElement.ValueKind != JsonValueKind.Array) ||
            (boundsElement.GetArrayLength() != 4)
        ) {
            throw new CanaryManifestRefusal(message: $"{context} {member} must be an array of four channel values (r, g, b, a).");
        }

        var bounds = new double[4];

        for (var index = 0; (index < 4); index++) {
            var item = boundsElement[index];

            if (
                (item.ValueKind != JsonValueKind.Number) ||
                !item.TryGetDouble(value: out var value) ||
                !double.IsFinite(d: value) ||
                (value < 0) ||
                (value > 1)
            ) {
                throw new CanaryManifestRefusal(message: $"{context} {member}[{index}] must be a normalized channel value in [0, 1].");
            }

            bounds[index] = value;
        }

        return bounds;
    }
    private static CanaryImageRegionAssertion ReadImageRegionAssertion(JsonElement element, string context) {
        CliStrictJson.RequireOnlyMembers(
            element: element,
            context: context,
            unknownMemberDetail: UnknownMemberDetail,
            refusal: Refusal,
            "capture",
            "extent",
            "holds",
            "maximum",
            "minimum",
            "name",
            "reduce",
            "region",
            "toleranceCodes",
            "type"
        );

        var name = CliStrictJson.ReadRequiredString(
            context: context,
            element: element,
            member: "name",
            refusal: Refusal
        );
        var capture = ReadRunRelativePath(
            context: context,
            element: element,
            member: "capture"
        );
        var extent = CliStrictJson.ReadRequiredArray(
            context: context,
            element: element,
            member: "extent",
            refusal: Refusal
        );

        if (
            (extent.GetArrayLength() != 2) ||
            !extent[0].TryGetInt32(value: out var width) ||
            !extent[1].TryGetInt32(value: out var height) ||
            (width <= 0) ||
            (height <= 0)
        ) {
            throw new CanaryManifestRefusal(message: $"{context} extent must be two positive whole pixel counts, [width, height]; the capture's extent is part of the claim.");
        }

        var region = CliStrictJson.ReadRequiredArray(
            context: context,
            element: element,
            member: "region",
            refusal: Refusal
        );
        var edges = new double[4];

        for (var index = 0; (index < 4); index++) {
            if (
                (region.GetArrayLength() != 4) ||
                (region[index].ValueKind != JsonValueKind.Number) ||
                !region[index].TryGetDouble(value: out edges[index]) ||
                !double.IsFinite(d: edges[index]) ||
                (edges[index] < 0) ||
                (edges[index] > 1)
            ) {
                throw new CanaryManifestRefusal(message: $"{context} region must be four normalized edges in [0, 1], [left, top, right, bottom].");
            }
        }

        var (left, top, right, bottom) = (edges[0], edges[1], edges[2], edges[3]);

        if (CanaryAssertions.RegionPixelCount(
            bottom: bottom,
            height: height,
            left: left,
            right: right,
            top: top,
            width: width
        ) == 0) {
            throw new CanaryManifestRefusal(message: $"{context} region holds no pixel center at {width}x{height}; an empty region passes vacuously.");
        }

        var reduceText = CliStrictJson.ReadRequiredString(
            context: context,
            element: element,
            member: "reduce",
            refusal: Refusal
        );
        var reduce = reduceText switch {
            "every" => CanaryImageReduce.Every,
            "mean" => CanaryImageReduce.Mean,
            _ => throw new CanaryManifestRefusal(message: $"{context} reduce '{reduceText}' is invalid; use exactly 'every' or 'mean' (casing is significant)."),
        };
        var minimum = ReadChannelBounds(
            context: context,
            element: element,
            member: "minimum"
        );
        var maximum = ReadChannelBounds(
            context: context,
            element: element,
            member: "maximum"
        );

        if (
            (minimum is null) &&
            (maximum is null)
        ) {
            throw new CanaryManifestRefusal(message: $"{context} needs minimum, maximum, or both; a region with no bound observes nothing.");
        }
        if (
            (minimum is not null) &&
            (maximum is not null) &&
            Enumerable.Range(
            count: 4,
            start: 0
        ).Any(predicate: index => (minimum[index] > maximum[index]))
        ) {
            throw new CanaryManifestRefusal(message: $"{context} minimum exceeds maximum in some channel.");
        }

        var toleranceCodes = CliStrictJson.ReadRequiredInt32(
            context: context,
            element: element,
            member: "toleranceCodes",
            refusal: Refusal
        );

        if (toleranceCodes is < 0 or > 255) {
            throw new CanaryManifestRefusal(message: $"{context} toleranceCodes must be a whole number of 8-bit codes in 0..255.");
        }

        // Stated, never defaulted, like framesAgree's agree: the two directions are opposite proofs.
        if (!element.TryGetProperty(
            propertyName: "holds",
            value: out var holdsElement
        )) {
            throw new CanaryManifestRefusal(message: $"{context} imageRegion must state holds explicitly as true or false.");
        }

        var holds = holdsElement.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new CanaryManifestRefusal(message: $"{context} holds must be true or false."),
        };

        return new CanaryImageRegionAssertion(
            Bottom: bottom,
            Capture: capture,
            Height: height,
            Holds: holds,
            Left: left,
            Maximum: maximum,
            Minimum: minimum,
            Name: name,
            Reduce: reduce,
            Right: right,
            ToleranceCodes: toleranceCodes,
            Top: top,
            Width: width
        );
    }
}
