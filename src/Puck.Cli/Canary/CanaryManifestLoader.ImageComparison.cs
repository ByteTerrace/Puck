using System.Text.Json;

namespace Puck.Cli.Canary;

internal static partial class CanaryManifestLoader {
    private static CanaryImageComparisonAssertion ReadImageComparisonAssertion(JsonElement element, string context) {
        CliStrictJson.RequireOnlyMembers(element: element, context: context, unknownMemberDetail: UnknownMemberDetail,
            refusal: Refusal, "type", "name", "capture", "reference", "referenceLeg", "referenceScale", "extent", "region", "maxErrorCodes", "meanErrorCodes", "holds");
        var name = CliStrictJson.ReadRequiredString(element: element, context: context, member: "name", refusal: Refusal);
        var capture = ReadRunRelativePath(element: element, context: context, member: "capture");
        var reference = ReadRunRelativePath(element: element, context: context, member: "reference");
        var otherLeg = false;
        if (element.TryGetProperty(propertyName: "referenceLeg", value: out var leg)) {
            if (leg.ValueKind != JsonValueKind.String || leg.GetString() is not ("current" or "other")) {
                throw new CanaryManifestRefusal(message: $"{context} referenceLeg must be exactly 'current' or 'other'.");
            }
            otherLeg = leg.GetString() == "other";
        }
        var scale = CliStrictJson.ReadRequiredInt32(element: element, context: context, member: "referenceScale", refusal: Refusal);
        var (width, height, left, top, right, bottom) = ReadImageSelection(element: element, context: context);
        if (scale < 1 || (long)width * scale > int.MaxValue || (long)height * scale > int.MaxValue) {
            throw new CanaryManifestRefusal(message: $"{context} referenceScale must be a positive integer whose scaled extent fits whole pixel counts.");
        }
        double Limit(string member) {
            if (!element.TryGetProperty(propertyName: member, value: out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(value: out var limit) || !double.IsFinite(limit) || limit < 0 || limit > 255) {
                throw new CanaryManifestRefusal(message: $"{context} {member} must state a finite RGB code error in 0..255.");
            }
            return limit;
        }
        var max = Limit(member: "maxErrorCodes");
        var mean = Limit(member: "meanErrorCodes");
        if (!element.TryGetProperty(propertyName: "holds", value: out var holdsElement) || holdsElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
            throw new CanaryManifestRefusal(message: $"{context} imageComparison must state holds explicitly as true or false.");
        }
        return new CanaryImageComparisonAssertion(Bottom: bottom, Capture: capture, Height: height, Holds: holdsElement.GetBoolean(),
            Left: left, MaxErrorCodes: max, MeanErrorCodes: mean, Name: name, Reference: reference, ReferenceScale: scale,
            Right: right, Top: top, Width: width) { ReferenceOtherLeg = otherLeg };
    }
}
