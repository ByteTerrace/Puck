using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Puck.World.Protocol;

namespace Puck.World.Addons;

/// <summary>
/// The addon mutation seam's stage 6 decoder (see <see cref="WorldAddonRuntime"/>'s <c>ResolveMutations</c>): turns
/// one guest-submitted JSON payload — already copied host-side out of guest linear memory by stage 5's
/// pointer-safety gate — into a typed <see cref="WorldMutation"/>. Hand-walked <see cref="JsonDocument"/>, the same
/// discipline <c>Puck.World.Client.Sdf.SdfDocumentDecoder</c> uses for its own untrusted-shaped door: every object's
/// members are collected once (a repeated key refuses, never silently resolves to whichever <c>JsonSerializer</c>
/// would have picked), checked against a per-kind allowed-member list (an unrecognized key refuses by name, never
/// ignored), and every scalar is finite-and-signedness-checked before it is trusted. Never source-gen POCO
/// deserialization — see the seam's own doc for why.
/// </summary>
/// <remarks>Wires the 5 HUD kinds
/// (<see cref="WorldMutation.UpsertHudPanel"/>/<see cref="WorldMutation.RemoveHudPanel"/>/
/// <see cref="WorldMutation.UpsertHudElement"/>/<see cref="WorldMutation.RemoveHudElement"/>/
/// <see cref="WorldMutation.SetHudDefaults"/>) — the ones <c>wasm/puck-addon-hudbuilder</c> exercises — plus the
/// state section's two kinds (<see cref="WorldMutation.UpsertStateRow"/>/<see cref="WorldMutation.RemoveStateRow"/>)
/// and the placement rows (<see cref="WorldMutation.UpsertPlacement"/>/<see cref="WorldMutation.RemovePlacement"/>,
/// the full <see cref="WorldPlacement"/> wire shape the document validator accepts — every facet: transform, distribution,
/// mirror, emission, solid, inhabit, faceSources, region, attach), plus <see cref="WorldMutation.SetInputHold"/>. Other
/// declared kinds have no entry yet;
/// <see cref="TryDecode"/> refuses an unwired ordinal by name rather than guessing a shape for it, so wiring one in
/// is strictly additive — a new <c>case</c> arm, never a change to this method's own contract.</remarks>
internal static class WorldAddonMutationDecoder {
    private static readonly string[] RectMembers = ["x", "y", "width", "height"];
    private static readonly string[] ElementMembers = ["id", "kind", "rect", "style", "text", "binding"];
    private static readonly string[] PanelMembers = ["id", "rect", "layer", "style", "elements"];
    private static readonly string[] RemovePanelMembers = ["id"];
    private static readonly string[] RemoveElementMembers = ["panelId", "elementId"];
    private static readonly string[] SetDefaultsMembers = ["enabled", "cursor"];
    private static readonly string[] SetDefaultsCursorMembers = ["hoverRadius", "sizePx", "role"];
    // The state section's two kinds — the whole-row shape mirrors world.row.set state's own inline-JSON grammar
    // (WorldStateRowJsonConverter's ONE authored shape), never a bespoke addon-only encoding: one member list, no
    // $type discriminator, `value` xor `cells`.
    private static readonly string[] RemoveStateRowMembers = ["name"];
    private static readonly string[] StateRowMembers = ["name", "kind", "value", "cells", "min", "max", "capacity", "nonNegative"];
    private static readonly string[] StateCellMembers = ["key", "value"];
    private static readonly Dictionary<string, CellKind> CellKinds = new(comparer: StringComparer.Ordinal) {
        ["int"] = CellKind.Int,
        ["fixed"] = CellKind.Fixed,
        ["bool"] = CellKind.Bool,
        ["text"] = CellKind.Text,
    };
    private static readonly string[] InputHoldMembers = ["ceilingTicks", "lowerAfterTicks", "defaultTicks", "equalizeByDefault", "participants"];
    private static readonly string[] InputHoldParticipantMembers = ["bodyIndex", "ticks", "equalized"];
    // The placement rows — the FULL WorldPlacement wire shape (every facet), plus each facet's own object shape.
    private static readonly string[] PlacementMembers = ["id", "prototypeId", "position", "yawDegrees", "scale", "distribution", "mirror", "emission", "solid", "inhabit", "faceSources", "region", "attach"];
    private static readonly string[] RemovePlacementMembers = ["id"];
    private static readonly string[] DistributionMembers = ["region", "fill"];
    private static readonly string[] DiscRegionMembers = ["$type", "radius", "sampleCount"];
    private static readonly string[] PointsRegionMembers = ["$type", "names", "halfExtent"];
    private static readonly string[] LatticeRegionMembers = ["$type", "stepA", "countA", "stepB", "countB"];
    private static readonly string[] SequenceMembers = ["name", "offset", "step"];
    private static readonly string[] MirrorMembers = ["normal", "offset"];
    private static readonly string[] EmissionMembers = ["patchId", "level", "radius"];
    private static readonly string[] SolidMembers = ["margin"];
    private static readonly string[] RegionMembers = ["radius"];
    private static readonly string[] AttachMembers = ["bodyIndex", "localOffset", "localYawDegrees"];
    private static readonly string[] InhabitMembers = ["kit", "look", "source", "count", "distribution"];
    private static readonly string[] FaceSourceMembers = ["face", "source"];
    private static readonly string[] FeedProfileMembers = ["width", "height", "refreshRateHz"];
    // WorldScreenSource's eight $type-discriminated variants (FaceSources[].source) — each variant's own allowed-
    // member list includes "$type" itself, since UniqueMembers keeps it in the same dictionary the field reads walk.
    private static readonly string[] ScreenSourceNoneMembers = ["$type"];
    private static readonly string[] ScreenSourceTestPatternMembers = ["$type", "width", "height"];
    private static readonly string[] ScreenSourceMachineMembers = ["$type", "engine", "contentPath", "options"];
    private static readonly string[] ScreenSourceCameraMembers = ["$type", "profile"];
    private static readonly string[] ScreenSourceViewMembers = ["$type", "cameraName"];
    private static readonly string[] ScreenSourceCaptureMembers = ["$type", "windowTitle", "profile", "monitorIndex"];
    private static readonly string[] ScreenSourceConsoleMembers = ["$type", "rows", "columns", "procedural"];
    private static readonly string[] ScreenSourceQrMembers = ["$type", "payload", "ecLevel", "quietZoneModules"];
    private static readonly Dictionary<string, WorldHudElementKind> ElementKinds = new(comparer: StringComparer.Ordinal) {
        ["rect"] = WorldHudElementKind.Rect,
        ["text"] = WorldHudElementKind.Text,
        ["gauge"] = WorldHudElementKind.Gauge,
    };
    private static readonly Dictionary<string, WorldHudLayer> Layers = new(comparer: StringComparer.Ordinal) {
        ["under"] = WorldHudLayer.Under,
        ["over"] = WorldHudLayer.Over,
        ["replace"] = WorldHudLayer.Replace,
    };
    private static readonly Dictionary<string, WorldHudPanelStyle> PanelStyles = new(comparer: StringComparer.Ordinal) {
        ["panel"] = WorldHudPanelStyle.Panel,
        ["strip"] = WorldHudPanelStyle.Strip,
        ["chip"] = WorldHudPanelStyle.Chip,
    };
    private static readonly Dictionary<string, WorldHudStyleToken> StyleTokens = new(comparer: StringComparer.Ordinal) {
        ["primary"] = WorldHudStyleToken.Primary,
        ["dim"] = WorldHudStyleToken.Dim,
        ["accent"] = WorldHudStyleToken.Accent,
        ["positive"] = WorldHudStyleToken.Positive,
        ["warning"] = WorldHudStyleToken.Warning,
        ["danger"] = WorldHudStyleToken.Danger,
    };

    // BodyIndex's LOWER bound (non-negative) is structural, checked here like Region's radius positivity; its UPPER
    // bound is document-dependent (the world's authored population capacity) and stays with the validator, matching
    // Inhabit's own kit/look/count division of labor below. LocalYawDegrees is genuinely optional (0 rides the body's
    // exact facing), unlike Region's required radius.
    private static WorldPlacementAttach DecodeAttach(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an object");
        }

        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: AttachMembers,
            context: context,
            members: members
        );

        var bodyIndex = RequireInt32(
            context: context,
            members: members,
            name: "bodyIndex"
        );

        if (bodyIndex < 0) {
            throw new AddonMutationDecodeException(message: $"{context}: 'bodyIndex' must be non-negative (got {bodyIndex.ToString(provider: CultureInfo.InvariantCulture)})");
        }

        var localOffset = RequireVector3(
            context: context,
            members: members,
            name: "localOffset"
        );
        var localYawDegrees = 0f;

        if (members.TryGetValue(
            key: "localYawDegrees",
            value: out var yawElement
        )) {
            if (
                (yawElement.ValueKind != JsonValueKind.Number) ||
                !yawElement.TryGetSingle(value: out var value) ||
                !float.IsFinite(f: value)
            ) {
                throw new AddonMutationDecodeException(message: $"{context}: 'localYawDegrees' must be finite");
            }

            localYawDegrees = value;
        }

        return new WorldPlacementAttach(
            BodyIndex: bodyIndex,
            LocalOffset: localOffset,
            LocalYawDegrees: localYawDegrees
        );
    }
    private static WorldDistribution DecodeDistribution(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an object");
        }

        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: DistributionMembers,
            context: context,
            members: members
        );

        if (
            !members.TryGetValue(
            key: "region",
            value: out var region
        ) ||
            !members.TryGetValue(
            key: "fill",
            value: out var fill
        )
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: requires 'region' and 'fill'");
        }

        return new WorldDistribution(
            Region: DecodeDistributionRegion(
                context: $"{context}.region",
                element: region
            ),
            Fill: DecodeSequence(
                context: $"{context}.fill",
                element: fill
            )
        );
    }
    private static WorldDistributionRegion DecodeDistributionRegion(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an object");
        }

        var members = UniqueMembers(
            context: context,
            element: element
        );
        var type = RequireString(
            context: context,
            members: members,
            name: "$type"
        );

        switch (type) {
            case "disc": {
                    RequireNoUnknownMembers(
                        allowed: DiscRegionMembers,
                        context: context,
                        members: members
                    );
                    int? sampleCount = null;

                    if (
                        members.TryGetValue(
                        key: "sampleCount",
                        value: out var sampleElement
                    ) &&
                        (sampleElement.ValueKind != JsonValueKind.Null)
                    ) {
                        if (!sampleElement.TryGetInt32(value: out var parsed)) {
                            throw new AddonMutationDecodeException(message: $"{context}: 'sampleCount' must be an integer or null");
                        }
                        sampleCount = parsed;
                    }

                    return new WorldDistributionRegion.Disc(
                        Radius: RequireFinite(
                            context: context,
                            members: members,
                            name: "radius"
                        ),
                        SampleCount: sampleCount
                    );
                }
            case "points": {
                    RequireNoUnknownMembers(
                        allowed: PointsRegionMembers,
                        context: context,
                        members: members
                    );

                    if (
                        !members.TryGetValue(
                        key: "names",
                        value: out var namesElement
                    ) ||
                        (namesElement.ValueKind != JsonValueKind.Array)
                    ) {
                        throw new AddonMutationDecodeException(message: $"{context}: 'names' must be an array");
                    }

                    var names = new List<string>();

                    foreach (var name in namesElement.EnumerateArray()) {
                        if (name.ValueKind != JsonValueKind.String) {
                            throw new AddonMutationDecodeException(message: $"{context}: every 'names' entry must be a string");
                        }
                        names.Add(item: name.GetString()!);
                    }

                    return new WorldDistributionRegion.Points(
                        Names: names,
                        HalfExtent: RequireFinite(
                            context: context,
                            members: members,
                            name: "halfExtent"
                        )
                    );
                }
            case "lattice":
                RequireNoUnknownMembers(
                    allowed: LatticeRegionMembers,
                    context: context,
                    members: members
                );

                return new WorldDistributionRegion.Lattice(
                    StepA: RequireVector3(
                        context: context,
                        members: members,
                        name: "stepA"
                    ),
                    CountA: RequireInt32(
                        context: context,
                        members: members,
                        name: "countA"
                    ),
                    StepB: RequireVector3(
                        context: context,
                        members: members,
                        name: "stepB"
                    ),
                    CountB: RequireInt32(
                        context: context,
                        members: members,
                        name: "countB"
                    )
                );
            default:
                throw new AddonMutationDecodeException(message: $"{context}: '$type' names '{type}', which is not one of {{disc, points, lattice}}");
        }
    }
    private static WorldHudElement DecodeElement(JsonElement element, string context) {
        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: ElementMembers,
            context: context,
            members: members
        );

        var id = RequireString(
            context: context,
            members: members,
            name: "id"
        );
        var kind = RequireEnum(
            context: context,
            map: ElementKinds,
            members: members,
            name: "kind"
        );
        var rect = RequireRect(
            context: context,
            members: members,
            name: "rect"
        );
        var style = RequireEnum(
            context: context,
            map: StyleTokens,
            members: members,
            name: "style"
        );
        string? text = null;
        string? binding = null;

        if (members.TryGetValue(
            key: "text",
            value: out var textElement
        )) {
            if (textElement.ValueKind != JsonValueKind.String) {
                throw new AddonMutationDecodeException(message: $"{context}: 'text' must be a string");
            }

            text = textElement.GetString();
        }

        if (members.TryGetValue(
            key: "binding",
            value: out var bindingElement
        )) {
            if (bindingElement.ValueKind != JsonValueKind.String) {
                throw new AddonMutationDecodeException(message: $"{context}: 'binding' must be a string");
            }

            binding = bindingElement.GetString();
        }

        return new WorldHudElement(
            Id: id,
            Kind: kind,
            Rect: rect,
            Style: style,
            Text: text,
            Binding: binding
        );
    }
    // Patch resolution (does patchId name a declared WorldPatch row?) is document-dependent and stays with the
    // validator — this only type-checks the facet's own scalars, matching Solid/Region/Inhabit's division of labor
    // below.
    private static WorldEmission DecodeEmission(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an object");
        }

        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: EmissionMembers,
            context: context,
            members: members
        );

        var patchId = RequireString(
            context: context,
            members: members,
            name: "patchId"
        );
        var level = RequireFinite(
            context: context,
            members: members,
            name: "level"
        );
        float? radius = null;

        if (members.TryGetValue(
            key: "radius",
            value: out var radiusElement
        )) {
            if (
                (radiusElement.ValueKind != JsonValueKind.Number) ||
                !radiusElement.TryGetSingle(value: out var value) ||
                !float.IsFinite(f: value)
            ) {
                throw new AddonMutationDecodeException(message: $"{context}: 'radius' must be finite");
            }

            radius = value;
        }

        return new WorldEmission(
            Level: level,
            PatchId: patchId,
            Radius: radius
        );
    }
    // Face-name resolution (does the creation declare this face?) is document-dependent and stays with the
    // validator.
    private static WorldPlacementFace DecodeFaceSource(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an object");
        }

        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: FaceSourceMembers,
            context: context,
            members: members
        );

        var face = RequireString(
            context: context,
            members: members,
            name: "face"
        );

        if (
            !members.TryGetValue(
            key: "source",
            value: out var sourceElement
        ) ||
            (sourceElement.ValueKind != JsonValueKind.Object)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: 'source' must be an object");
        }

        var source = DecodeScreenSource(
            context: $"{context}.source",
            element: sourceElement
        );

        return new WorldPlacementFace(
            Face: face,
            Source: source
        );
    }
    private static WorldFeedProfile DecodeFeedProfile(JsonElement element, string context) {
        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: FeedProfileMembers,
            context: context,
            members: members
        );

        var width = RequireInt32(
            context: context,
            members: members,
            name: "width"
        );
        var height = RequireInt32(
            context: context,
            members: members,
            name: "height"
        );
        var refreshRateHz = RequireUInt32(
            context: context,
            members: members,
            name: "refreshRateHz"
        );

        return new WorldFeedProfile(
            Height: height,
            RefreshRateHz: refreshRateHz,
            Width: width
        );
    }
    // Kit/look name resolution and population-wide bounds stay with the document validator.
    private static WorldPlacementInhabit DecodeInhabit(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an object");
        }

        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: InhabitMembers,
            context: context,
            members: members
        );

        string? kit = null;
        string? look = null;

        if (members.TryGetValue(
            key: "kit",
            value: out var kitElement
        )) {
            if (kitElement.ValueKind != JsonValueKind.String) {
                throw new AddonMutationDecodeException(message: $"{context}: 'kit' must be a string");
            }

            kit = kitElement.GetString();
        }

        if (members.TryGetValue(
            key: "look",
            value: out var lookElement
        )) {
            if (lookElement.ValueKind != JsonValueKind.String) {
                throw new AddonMutationDecodeException(message: $"{context}: 'look' must be a string");
            }

            look = lookElement.GetString();
        }

        var source = RequireIntentSource(
            context: context,
            members: members
        );
        var count = 1;

        if (members.TryGetValue(
            key: "count",
            value: out var countElement
        )) {
            if (
                (countElement.ValueKind != JsonValueKind.Number) ||
                !countElement.TryGetInt32(value: out count)
            ) {
                throw new AddonMutationDecodeException(message: $"{context}: 'count' must be an integer");
            }
        }

        if (count < 1) {
            throw new AddonMutationDecodeException(message: $"{context}: 'count' must be at least 1 (got {count.ToString(provider: CultureInfo.InvariantCulture)})");
        }

        var distribution = (members.TryGetValue(
            key: "distribution",
            value: out var distributionElement
        )
            ? DecodeDistribution(
                context: $"{context}.distribution",
                element: distributionElement
            )
            : null
        );

        return new WorldPlacementInhabit(
            Count: count,
            Distribution: distribution,
            Kit: kit,
            Look: look,
            Source: source
        );
    }
    private static WorldPlacementMirror DecodeMirror(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an object");
        }

        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: MirrorMembers,
            context: context,
            members: members
        );

        return new WorldPlacementMirror(
            Normal: RequireVector3(
                context: context,
                members: members,
                name: "normal"
            ),
            Offset: RequireFinite(
                context: context,
                members: members,
                name: "offset"
            )
        );
    }
    // Radius finite-and-positive mirrors WorldPlacementRegion's own doc ("must be finite and positive") — the SAME
    // structural-invariant precedent RequireRect's width/height>0 checks already set for this decoder.
    private static WorldPlacementRegion DecodeRegion(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an object");
        }

        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: RegionMembers,
            context: context,
            members: members
        );

        var radius = RequireFinite(
            context: context,
            members: members,
            name: "radius"
        );

        if (radius <= 0f) {
            throw new AddonMutationDecodeException(message: $"{context}: 'radius' must be positive (got {radius.ToString(provider: CultureInfo.InvariantCulture)})");
        }

        return new WorldPlacementRegion(Radius: radius);
    }
    private static WorldMutation DecodeRemoveHudElement(JsonElement root, WorldPrincipal principal) {
        var members = UniqueMembers(
            context: "RemoveHudElement",
            element: root
        );

        RequireNoUnknownMembers(
            allowed: RemoveElementMembers,
            context: "RemoveHudElement",
            members: members
        );

        var panelId = RequireString(
            context: "RemoveHudElement",
            members: members,
            name: "panelId"
        );
        var elementId = RequireString(
            context: "RemoveHudElement",
            members: members,
            name: "elementId"
        );

        return new WorldMutation.RemoveHudElement(
            ElementId: elementId,
            PanelId: panelId,
            Principal: principal
        );
    }
    private static WorldMutation DecodeRemoveHudPanel(JsonElement root, WorldPrincipal principal) {
        return DecodeSingleKeyRemoval(
            root: root,
            principal: principal,
            context: "RemoveHudPanel",
            allowed: RemovePanelMembers,
            keyName: "id",
            create: static (actor, key) => new WorldMutation.RemoveHudPanel(
                Id: key,
                Principal: actor
            )
        );
    }
    private static WorldMutation DecodeRemovePlacement(JsonElement root, WorldPrincipal principal) {
        return DecodeSingleKeyRemoval(
            root: root,
            principal: principal,
            context: "RemovePlacement",
            allowed: RemovePlacementMembers,
            keyName: "id",
            create: static (actor, key) => new WorldMutation.RemovePlacement(
                Id: key,
                Principal: actor
            )
        );
    }
    private static WorldMutation DecodeRemoveStateRow(JsonElement root, WorldPrincipal principal) {
        return DecodeSingleKeyRemoval(
            root: root,
            principal: principal,
            context: "RemoveStateRow",
            allowed: RemoveStateRowMembers,
            keyName: "name",
            create: static (actor, key) => new WorldMutation.RemoveStateRow(
                Name: key,
                Principal: actor
            )
        );
    }
    // WorldScreenSource's $type discriminators are the SAME strings the document's own JsonDerivedType attributes
    // declare (fixed regardless of naming policy) — the one place this decoder's own token vocabulary intentionally
    // matches the document wire exactly, because these are type discriminators, not enum values.
    private static WorldScreenSource DecodeScreenSource(JsonElement element, string context) {
        var members = UniqueMembers(
            context: context,
            element: element
        );
        var type = RequireString(
            context: context,
            members: members,
            name: "$type"
        );

        return type switch {
            "none" => DecodeScreenSourceNone(
            context: context,
            members: members
        ),
            "testPattern" => DecodeScreenSourceTestPattern(
            context: context,
            members: members
        ),
            "machine" => DecodeScreenSourceMachine(
            context: context,
            members: members
        ),
            "camera" => DecodeScreenSourceCamera(
            context: context,
            members: members
        ),
            "view" => DecodeScreenSourceView(
            context: context,
            members: members
        ),
            "capture" => DecodeScreenSourceCapture(
            context: context,
            members: members
        ),
            "console" => DecodeScreenSourceConsole(
            context: context,
            members: members
        ),
            "qr" => DecodeScreenSourceQr(
            context: context,
            members: members
        ),
            _ => throw new AddonMutationDecodeException(message: $"{context}: '$type' names '{type}', which is not one of {{none, testPattern, machine, camera, view, capture, console, qr}}"),
        };
    }
    private static WorldScreenSource DecodeScreenSourceCamera(Dictionary<string, JsonElement> members, string context) {
        RequireNoUnknownMembers(
            allowed: ScreenSourceCameraMembers,
            context: context,
            members: members
        );

        if (
            !members.TryGetValue(
            key: "profile",
            value: out var profileElement
        ) ||
            (profileElement.ValueKind != JsonValueKind.Object)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: 'profile' must be an object");
        }

        var profile = DecodeFeedProfile(
            context: $"{context}.profile",
            element: profileElement
        );

        return new WorldScreenSource.Camera(Profile: profile);
    }
    private static WorldScreenSource DecodeScreenSourceCapture(Dictionary<string, JsonElement> members, string context) {
        RequireNoUnknownMembers(
            allowed: ScreenSourceCaptureMembers,
            context: context,
            members: members
        );

        var windowTitle = RequireString(
            context: context,
            members: members,
            name: "windowTitle"
        );

        if (
            !members.TryGetValue(
            key: "profile",
            value: out var profileElement
        ) ||
            (profileElement.ValueKind != JsonValueKind.Object)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: 'profile' must be an object");
        }

        var profile = DecodeFeedProfile(
            context: $"{context}.profile",
            element: profileElement
        );
        int? monitorIndex = null;

        if (members.TryGetValue(
            key: "monitorIndex",
            value: out var monitorElement
        )) {
            if (
                (monitorElement.ValueKind != JsonValueKind.Number) ||
                !monitorElement.TryGetInt32(value: out var value)
            ) {
                throw new AddonMutationDecodeException(message: $"{context}: 'monitorIndex' must be an integer");
            }

            monitorIndex = value;
        }

        return new WorldScreenSource.Capture(
            MonitorIndex: monitorIndex,
            Profile: profile,
            WindowTitle: windowTitle
        );
    }
    private static WorldScreenSource DecodeScreenSourceConsole(Dictionary<string, JsonElement> members, string context) {
        RequireNoUnknownMembers(
            allowed: ScreenSourceConsoleMembers,
            context: context,
            members: members
        );

        var rows = 24;
        var columns = 64;
        var procedural = false;

        if (members.TryGetValue(
            key: "rows",
            value: out var rowsElement
        )) {
            if (
                (rowsElement.ValueKind != JsonValueKind.Number) ||
                !rowsElement.TryGetInt32(value: out rows)
            ) {
                throw new AddonMutationDecodeException(message: $"{context}: 'rows' must be an integer");
            }
        }

        if (members.TryGetValue(
            key: "columns",
            value: out var columnsElement
        )) {
            if (
                (columnsElement.ValueKind != JsonValueKind.Number) ||
                !columnsElement.TryGetInt32(value: out columns)
            ) {
                throw new AddonMutationDecodeException(message: $"{context}: 'columns' must be an integer");
            }
        }

        if (members.TryGetValue(
            key: "procedural",
            value: out var proceduralElement
        )) {
            if (proceduralElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
                throw new AddonMutationDecodeException(message: $"{context}: 'procedural' must be a boolean");
            }

            procedural = proceduralElement.GetBoolean();
        }

        return new WorldScreenSource.Console(
            Columns: columns,
            Procedural: procedural,
            Rows: rows
        );
    }
    private static WorldScreenSource DecodeScreenSourceMachine(Dictionary<string, JsonElement> members, string context) {
        RequireNoUnknownMembers(
            allowed: ScreenSourceMachineMembers,
            context: context,
            members: members
        );

        var engine = RequireString(
            context: context,
            members: members,
            name: "engine"
        );
        var contentPath = RequireString(
            context: context,
            members: members,
            name: "contentPath"
        );
        string? options = null;

        if (members.TryGetValue(
            key: "options",
            value: out var optionsElement
        )) {
            if (optionsElement.ValueKind != JsonValueKind.String) {
                throw new AddonMutationDecodeException(message: $"{context}: 'options' must be a string");
            }

            options = optionsElement.GetString();
        }

        return new WorldScreenSource.Machine(
            ContentPath: contentPath,
            Engine: engine,
            Options: options
        );
    }
    private static WorldScreenSource DecodeScreenSourceNone(Dictionary<string, JsonElement> members, string context) {
        RequireNoUnknownMembers(
            allowed: ScreenSourceNoneMembers,
            context: context,
            members: members
        );

        return new WorldScreenSource.None();
    }
    private static WorldScreenSource DecodeScreenSourceQr(Dictionary<string, JsonElement> members, string context) {
        RequireNoUnknownMembers(
            allowed: ScreenSourceQrMembers,
            context: context,
            members: members
        );

        var payload = RequireString(
            context: context,
            members: members,
            name: "payload"
        );
        var ecLevel = "M";
        var quietZoneModules = 4;

        if (members.TryGetValue(
            key: "ecLevel",
            value: out var ecLevelElement
        )) {
            if (ecLevelElement.ValueKind != JsonValueKind.String) {
                throw new AddonMutationDecodeException(message: $"{context}: 'ecLevel' must be a string");
            }

            ecLevel = ecLevelElement.GetString()!;
        }

        if (members.TryGetValue(
            key: "quietZoneModules",
            value: out var quietZoneElement
        )) {
            if (
                (quietZoneElement.ValueKind != JsonValueKind.Number) ||
                !quietZoneElement.TryGetInt32(value: out quietZoneModules)
            ) {
                throw new AddonMutationDecodeException(message: $"{context}: 'quietZoneModules' must be an integer");
            }
        }

        // The letter/capacity checks are NOT repeated here: this decoder only shapes the row, and
        // WorldDefinitionValidator gates the composed candidate before any mutation applies.
        return new WorldScreenSource.Qr(
            EcLevel: ecLevel,
            Payload: payload,
            QuietZoneModules: quietZoneModules
        );
    }
    private static WorldScreenSource DecodeScreenSourceTestPattern(Dictionary<string, JsonElement> members, string context) {
        RequireNoUnknownMembers(
            allowed: ScreenSourceTestPatternMembers,
            context: context,
            members: members
        );

        var width = RequireInt32(
            context: context,
            members: members,
            name: "width"
        );
        var height = RequireInt32(
            context: context,
            members: members,
            name: "height"
        );

        return new WorldScreenSource.TestPattern(
            Height: height,
            Width: width
        );
    }
    private static WorldScreenSource DecodeScreenSourceView(Dictionary<string, JsonElement> members, string context) {
        RequireNoUnknownMembers(
            allowed: ScreenSourceViewMembers,
            context: context,
            members: members
        );

        var cameraName = RequireString(
            context: context,
            members: members,
            name: "cameraName"
        );

        return new WorldScreenSource.View(CameraName: cameraName);
    }
    private static WorldSequence DecodeSequence(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an object");
        }

        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: SequenceMembers,
            context: context,
            members: members
        );

        return new WorldSequence(
            Name: RequireString(
                context: context,
                members: members,
                name: "name"
            ),
            Offset: RequireInt32(
                context: context,
                members: members,
                name: "offset"
            ),
            Step: RequireFinite(
                context: context,
                members: members,
                name: "step"
            )
        );
    }
    private static WorldMutation DecodeSetHudDefaults(JsonElement root, WorldPrincipal principal) {
        var members = UniqueMembers(
            context: "SetHudDefaults",
            element: root
        );

        RequireNoUnknownMembers(
            allowed: SetDefaultsMembers,
            context: "SetHudDefaults",
            members: members
        );

        if (
            !members.TryGetValue(
            key: "enabled",
            value: out var enabledElement
        ) ||
            (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        ) {
            throw new AddonMutationDecodeException(message: "SetHudDefaults: 'enabled' must be a boolean");
        }

        // The optional cursor policy — whole-row replace semantics, exactly like the console's world.row.set
        // hud.defaults door: an act authored without it clears any earlier policy back to the built-in default.
        // Range/role sanity beyond finiteness is WorldDefinitionValidator's job (the HUD decoders leave
        // capacity/authoring-policy checks to the validate stage).
        WorldHudCursor? cursor = null;

        if (members.TryGetValue(
            key: "cursor",
            value: out var cursorElement
        )) {
            if (cursorElement.ValueKind != JsonValueKind.Object) {
                throw new AddonMutationDecodeException(message: "SetHudDefaults: 'cursor' must be an object");
            }

            var cursorMembers = UniqueMembers(
                context: "SetHudDefaults.cursor",
                element: cursorElement
            );

            RequireNoUnknownMembers(
                allowed: SetDefaultsCursorMembers,
                context: "SetHudDefaults.cursor",
                members: cursorMembers
            );

            var roleToken = RequireString(
                context: "SetHudDefaults.cursor",
                members: cursorMembers,
                name: "role"
            );

            if (
                !Enum.TryParse<WorldHudCursorRole>(
                ignoreCase: false,
                result: out var role,
                value: roleToken
            ) ||
                !Enum.IsDefined(value: role)
            ) {
                throw new AddonMutationDecodeException(message: $"SetHudDefaults: cursor.role '{roleToken}' is not a cursor role token");
            }

            cursor = new WorldHudCursor(
                HoverRadius: RequireFinite(
                    context: "SetHudDefaults.cursor",
                    members: cursorMembers,
                    name: "hoverRadius"
                ),
                SizePx: RequireFinite(
                    context: "SetHudDefaults.cursor",
                    members: cursorMembers,
                    name: "sizePx"
                ),
                Role: role
            );
        }

        return new WorldMutation.SetHudDefaults(
            Principal: principal,
            Defaults: new WorldHudDefaults(
                Enabled: enabledElement.GetBoolean(),
                Cursor: cursor
            )
        );
    }
    private static WorldMutation DecodeSetInputHold(JsonElement root, WorldPrincipal principal) {
        var members = UniqueMembers(
            context: "SetInputHold",
            element: root
        );

        RequireNoUnknownMembers(
            allowed: InputHoldMembers,
            context: "SetInputHold",
            members: members
        );

        if (
            !members.Remove(
            key: "participants",
            value: out var participantsElement
        ) ||
            (participantsElement.ValueKind != JsonValueKind.Array)
        ) {
            throw new AddonMutationDecodeException(message: "SetInputHold: 'participants' must be an array");
        }

        var participants = new List<WorldInputHoldParticipant>();
        var index = 0;

        foreach (var element in participantsElement.EnumerateArray()) {
            var context = $"SetInputHold.participants[{index}]";

            if (element.ValueKind != JsonValueKind.Object) {
                throw new AddonMutationDecodeException(message: $"{context} must be an object");
            }

            var participantMembers = UniqueMembers(
                context: context,
                element: element
            );

            RequireNoUnknownMembers(
                allowed: InputHoldParticipantMembers,
                context: context,
                members: participantMembers
            );
            participants.Add(item: new WorldInputHoldParticipant(
                BodyIndex: RequireInt32(
                    context: context,
                    members: participantMembers,
                    name: "bodyIndex"
                ),
                Ticks: RequireInt32(
                    context: context,
                    members: participantMembers,
                    name: "ticks"
                ),
                Equalized: RequireBool(
                    context: context,
                    members: participantMembers,
                    name: "equalized"
                )
            ));
            index++;
        }

        return new WorldMutation.SetInputHold(
            Principal: principal,
            Settings: new WorldInputHoldSettings(
                CeilingTicks: RequireInt32(
                    context: "SetInputHold",
                    members: members,
                    name: "ceilingTicks"
                ),
                LowerAfterTicks: RequireInt32(
                    context: "SetInputHold",
                    members: members,
                    name: "lowerAfterTicks"
                ),
                DefaultTicks: RequireInt32(
                    context: "SetInputHold",
                    members: members,
                    name: "defaultTicks"
                ),
                EqualizeByDefault: RequireBool(
                    context: "SetInputHold",
                    members: members,
                    name: "equalizeByDefault"
                ),
                Participants: participants
            )
        );
    }
    /// <summary>Owns the wire shape shared by mutations that remove one row by a required string key.</summary>
    private static WorldMutation DecodeSingleKeyRemoval(
        JsonElement root,
        WorldPrincipal principal,
        string context,
        IReadOnlyList<string> allowed,
        string keyName,
        Func<WorldPrincipal, string, WorldMutation> create) {
        var members = UniqueMembers(
            context: context,
            element: root
        );

        RequireNoUnknownMembers(
            allowed: allowed,
            context: context,
            members: members
        );

        var key = RequireString(
            context: context,
            members: members,
            name: keyName
        );

        return create(
            arg1: principal,
            arg2: key
        );
    }
    // The field-provider-vs-analytic gate is document-dependent (needs the live collision section) and stays with
    // the validator.
    private static WorldSolid DecodeSolid(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an object");
        }

        var members = UniqueMembers(
            context: context,
            element: element
        );

        RequireNoUnknownMembers(
            allowed: SolidMembers,
            context: context,
            members: members
        );

        var margin = RequireFinite(
            context: context,
            members: members,
            name: "margin"
        );

        return new WorldSolid(Margin: margin);
    }
    // A fixed-kind value is DECIMAL TEXT here, exactly as the document and the console verb spell it — the addon
    // wire's raw-bits convention covers the ABI's numeric channel cells (WorldMutation.UpsertStateCell.Value), never
    // this JSON payload, which is the SAME grammar world.row.set state takes and must not fork from it.
    private static WorldStateCell DecodeStateCell(CellName key, JsonElement element, CellKind kind, string context) {
        switch (kind) {
            case CellKind.Text:
                if (element.ValueKind != JsonValueKind.String) {
                    throw new AddonMutationDecodeException(message: $"{context}: must be a string");
                }

                return new WorldStateCell(
                    Key: key,
                    Text: (element.GetString() ?? string.Empty)
                );
            case CellKind.Bool:
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
                    throw new AddonMutationDecodeException(message: $"{context}: must be a boolean");
                }

                return new WorldStateCell(
                    Key: key,
                    Value: (element.GetBoolean()
                    ? 1
                    : 0)
                );
            default:
                return new WorldStateCell(
                    Key: key,
                    Value: RequireStateNumber(
                        context: context,
                        element: element,
                        kind: kind
                    )
                );
        }
    }
    private static List<WorldStateCell> DecodeStateCells(JsonElement element, CellKind kind, string context) {
        if (element.ValueKind != JsonValueKind.Array) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an array of {{\"key\", \"value\"}} objects");
        }

        var cells = new List<WorldStateCell>();
        var index = 0;

        foreach (var entry in element.EnumerateArray()) {
            var cellContext = $"{context}[{index}]";

            if (entry.ValueKind != JsonValueKind.Object) {
                throw new AddonMutationDecodeException(message: $"{cellContext}: must be an object");
            }

            var members = UniqueMembers(
                context: cellContext,
                element: entry
            );

            RequireNoUnknownMembers(
                allowed: StateCellMembers,
                context: cellContext,
                members: members
            );

            var key = RequireString(
                context: cellContext,
                members: members,
                name: "key"
            );
            var cellKey = RequireCellName(
                candidate: key,
                context: $"{cellContext}.key"
            );

            if (!members.TryGetValue(
                key: "value",
                value: out var valueElement
            )) {
                throw new AddonMutationDecodeException(message: $"{cellContext}: requires 'value'");
            }

            cells.Add(item: DecodeStateCell(
                context: $"{cellContext}.value",
                element: valueElement,
                key: cellKey,
                kind: kind
            ));
            index++;
        }

        return cells;
    }
    private static WorldMutation DecodeUpsertHudElement(JsonElement root, WorldPrincipal principal) {
        var members = UniqueMembers(
            context: "UpsertHudElement",
            element: root
        );

        RequireNoUnknownMembers(
            allowed: ["panelId", "element"],
            context: "UpsertHudElement",
            members: members
        );

        var panelId = RequireString(
            context: "UpsertHudElement",
            members: members,
            name: "panelId"
        );

        if (
            !members.TryGetValue(
            key: "element",
            value: out var elementValue
        ) ||
            (elementValue.ValueKind != JsonValueKind.Object)
        ) {
            throw new AddonMutationDecodeException(message: "UpsertHudElement: 'element' must be an object");
        }

        var element = DecodeElement(
            context: "UpsertHudElement.element",
            element: elementValue
        );

        return new WorldMutation.UpsertHudElement(
            Element: element,
            PanelId: panelId,
            Principal: principal
        );
    }
    private static WorldMutation DecodeUpsertHudPanel(JsonElement root, WorldPrincipal principal) {
        var members = UniqueMembers(
            context: "UpsertHudPanel",
            element: root
        );

        RequireNoUnknownMembers(
            allowed: PanelMembers,
            context: "UpsertHudPanel",
            members: members
        );

        var id = RequireString(
            context: "UpsertHudPanel",
            members: members,
            name: "id"
        );
        var rect = RequireRect(
            context: "UpsertHudPanel",
            members: members,
            name: "rect"
        );
        var layer = RequireEnum(
            context: "UpsertHudPanel",
            map: Layers,
            members: members,
            name: "layer"
        );
        var style = RequireEnum(
            context: "UpsertHudPanel",
            map: PanelStyles,
            members: members,
            name: "style"
        );
        var elements = new List<WorldHudElement>();

        if (members.TryGetValue(
            key: "elements",
            value: out var elementsElement
        )) {
            if (elementsElement.ValueKind != JsonValueKind.Array) {
                throw new AddonMutationDecodeException(message: "UpsertHudPanel: 'elements' must be an array");
            }

            foreach (var element in elementsElement.EnumerateArray()) {
                elements.Add(item: DecodeElement(
                    context: "UpsertHudPanel.elements[]",
                    element: element
                ));
            }
        }

        return new WorldMutation.UpsertHudPanel(
            Principal: principal,
            Panel: new WorldHudPanel(
                Elements: elements,
                Id: id,
                Layer: layer,
                Rect: rect,
                Style: style
            )
        );
    }
    // ---- Placement rows (UpsertPlacement 19 / RemovePlacement 20) ----

    private static WorldMutation DecodeUpsertPlacement(JsonElement root, WorldPrincipal principal) {
        var members = UniqueMembers(
            context: "UpsertPlacement",
            element: root
        );

        RequireNoUnknownMembers(
            allowed: PlacementMembers,
            context: "UpsertPlacement",
            members: members
        );

        var id = RequireString(
            context: "UpsertPlacement",
            members: members,
            name: "id"
        );
        var prototypeId = RequireString(
            context: "UpsertPlacement",
            members: members,
            name: "prototypeId"
        );
        var position = RequireVector3(
            context: "UpsertPlacement",
            members: members,
            name: "position"
        );
        var yawDegrees = RequireFinite(
            context: "UpsertPlacement",
            members: members,
            name: "yawDegrees"
        );
        var scale = RequireFinite(
            context: "UpsertPlacement",
            members: members,
            name: "scale"
        );
        WorldDistribution? distribution = null;
        WorldPlacementMirror? mirror = null;
        WorldEmission? emission = null;
        WorldSolid? solid = null;
        WorldPlacementInhabit? inhabit = null;
        List<WorldPlacementFace>? faceSources = null;
        WorldPlacementRegion? region = null;
        WorldPlacementAttach? attach = null;

        if (members.TryGetValue(
            key: "distribution",
            value: out var distributionElement
        )) {
            distribution = DecodeDistribution(
                context: "UpsertPlacement.distribution",
                element: distributionElement
            );
        }

        if (members.TryGetValue(
            key: "mirror",
            value: out var mirrorElement
        )) {
            mirror = DecodeMirror(
                context: "UpsertPlacement.mirror",
                element: mirrorElement
            );
        }

        if (members.TryGetValue(
            key: "emission",
            value: out var emissionElement
        )) {
            emission = DecodeEmission(
                context: "UpsertPlacement.emission",
                element: emissionElement
            );
        }

        if (members.TryGetValue(
            key: "solid",
            value: out var solidElement
        )) {
            solid = DecodeSolid(
                context: "UpsertPlacement.solid",
                element: solidElement
            );
        }

        if (members.TryGetValue(
            key: "inhabit",
            value: out var inhabitElement
        )) {
            inhabit = DecodeInhabit(
                context: "UpsertPlacement.inhabit",
                element: inhabitElement
            );
        }

        if (members.TryGetValue(
            key: "faceSources",
            value: out var faceSourcesElement
        )) {
            if (faceSourcesElement.ValueKind != JsonValueKind.Array) {
                throw new AddonMutationDecodeException(message: "UpsertPlacement: 'faceSources' must be an array");
            }

            faceSources = [];

            foreach (var element in faceSourcesElement.EnumerateArray()) {
                faceSources.Add(item: DecodeFaceSource(
                    context: "UpsertPlacement.faceSources[]",
                    element: element
                ));
            }
        }

        if (members.TryGetValue(
            key: "region",
            value: out var regionElement
        )) {
            region = DecodeRegion(
                context: "UpsertPlacement.region",
                element: regionElement
            );
        }

        if (members.TryGetValue(
            key: "attach",
            value: out var attachElement
        )) {
            attach = DecodeAttach(
                context: "UpsertPlacement.attach",
                element: attachElement
            );
        }

        var placement = new WorldPlacement(
            Attach: attach,
            PrototypeId: prototypeId,
            Distribution: distribution,
            Emission: emission,
            FaceSources: faceSources,
            Id: id,
            Inhabit: inhabit,
            Mirror: mirror,
            Position: position,
            Region: region,
            Scale: scale,
            Solid: solid,
            YawDegrees: yawDegrees
        );

        return new WorldMutation.UpsertPlacement(
            Placement: placement,
            Principal: principal
        );
    }
    // ---- State section (UpsertStateRow 46 / RemoveStateRow 47) ----

    // One shape, matching WorldStateRowJsonConverter's exactly: a name, a kind, the optional envelope fields, and
    // either a bare `value` (sugar for the one cell keyed WorldStateRow.SlotKey) or a keyed `cells` array — one
    // list, one walk, no per-$type branching. Both-or-neither Min/Max, the value's own in-range check, and the
    // capacity/text-length ceilings are all WorldDefinitionValidator's job; this only turns wire scalars into the
    // typed row, exactly like the HUD decoders leave capacity/authoring-policy checks to the same validate stage.
    private static WorldMutation DecodeUpsertStateRow(JsonElement root, WorldPrincipal principal) {
        const string Context = "UpsertStateRow";

        var members = UniqueMembers(
            context: Context,
            element: root
        );

        RequireNoUnknownMembers(
            allowed: StateRowMembers,
            context: Context,
            members: members
        );

        var name = RequireString(
            context: Context,
            members: members,
            name: "name"
        );
        var rowName = RequireCellName(
            candidate: name,
            context: $"{Context}.name"
        );
        var kind = RequireEnum(
            context: Context,
            map: CellKinds,
            members: members,
            name: "kind"
        );
        var hasValue = members.TryGetValue(
            key: "value",
            value: out var valueElement
        );
        var hasCells = members.TryGetValue(
            key: "cells",
            value: out var cellsElement
        );
        var hasCapacity = members.TryGetValue(
            key: "capacity",
            value: out var capacityElement
        );

        if (
            hasValue &&
            hasCells
        ) {
            throw new AddonMutationDecodeException(message: $"{Context}: '{name}' declares both 'value' and 'cells' — 'value' IS the one-cell spelling of 'cells'; author one or the other");
        }

        if (
            hasValue &&
            hasCapacity
        ) {
            throw new AddonMutationDecodeException(message: $"{Context}: '{name}' declares 'value' beside 'capacity' — declaring a capacity is declaring a keyed row, whose cells are authored under 'cells'");
        }

        return new WorldMutation.UpsertStateRow(
            Principal: principal,
            Row: new WorldStateRow(
                Name: rowName,
                Kind: kind,
                Min: OptionalStateNumber(
                    context: Context,
                    kind: kind,
                    members: members,
                    name: "min"
                ),
                Max: OptionalStateNumber(
                    context: Context,
                    kind: kind,
                    members: members,
                    name: "max"
                ),
                Capacity: (hasCapacity
            ? (capacityElement.TryGetInt32(value: out var capacity)
                ? capacity
                : throw new AddonMutationDecodeException(message: $"{Context}: 'capacity' must be an integer"))
            : null),
                NonNegative: (members.TryGetValue(
                    key: "nonNegative",
                    value: out var nonNegativeElement
                ) && ((nonNegativeElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? nonNegativeElement.GetBoolean()
            : throw new AddonMutationDecodeException(message: $"{Context}: 'nonNegative' must be a boolean"))),
                Cells: (hasValue
            ? [DecodeStateCell(
                            context: $"{Context}.value",
                            element: valueElement,
                            key: WorldStateRow.SlotKey,
                            kind: kind
                        )]
            : (hasCells
                ? DecodeStateCells(
                            context: $"{Context}.cells",
                            element: cellsElement,
                            kind: kind
                        )
                : []))
            )
        );
    }
    private static long? OptionalInt64(Dictionary<string, JsonElement> members, string name, string context) {
        if (!members.TryGetValue(
            key: name,
            value: out var element
        )) {
            return null;
        }

        if (
            (element.ValueKind != JsonValueKind.Number) ||
            !element.TryGetInt64(value: out var value)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be an integer");
        }

        return value;
    }
    private static long? OptionalStateNumber(Dictionary<string, JsonElement> members, string name, CellKind kind, string context) =>
        (members.TryGetValue(
            key: name,
            value: out var element
        )
            ? RequireStateNumber(
                context: $"{context}.{name}",
                element: element,
                kind: kind
            )
            : null
        );
    private static bool RequireBool(Dictionary<string, JsonElement> members, string name, string context) {
        if (
            !members.TryGetValue(
            key: name,
            value: out var element
        ) ||
            (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be a boolean");
        }

        return element.GetBoolean();
    }
    // The one door this decoder validates a candidate row/cell name through — CellName refuses an empty, dotted,
    // or otherwise unsafe candidate BY NAME, so an addon-authored state row can never reach the substrate holding a
    // name the document's own JSON parse would have refused.
    private static CellName RequireCellName(string candidate, string context) =>
        (CellName.TryParse(
            candidate: candidate,
            name: out var name,
            reason: out var reason
        )
            ? name
            : throw new AddonMutationDecodeException(message: $"{context}: '{candidate}' {reason}")
        );
    private static TEnum RequireEnum<TEnum>(Dictionary<string, JsonElement> members, string name, Dictionary<string, TEnum> map, string context) where TEnum : struct, Enum {
        if (
            !members.TryGetValue(
            key: name,
            value: out var element
        ) ||
            (element.ValueKind != JsonValueKind.String)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be a string");
        }

        var token = (element.GetString() ?? "");

        if (!map.TryGetValue(
            key: token,
            value: out var value
        )) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' names '{token}', which is not one of {{{string.Join(
                separator: ", ",
                values: map.Keys
            )}}}");
        }

        return value;
    }
    private static float RequireFinite(Dictionary<string, JsonElement> members, string name, string context) {
        if (
            !members.TryGetValue(
            key: name,
            value: out var element
        ) ||
            (element.ValueKind != JsonValueKind.Number) ||
            !element.TryGetSingle(value: out var value)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be a number");
        }

        if (!float.IsFinite(f: value)) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be finite (got {value.ToString(provider: CultureInfo.InvariantCulture)})");
        }

        return value;
    }
    private static int RequireInt32(Dictionary<string, JsonElement> members, string name, string context) {
        var element = RequireNumberElement(
            context: context,
            expected: "an integer",
            members: members,
            name: name
        );

        if (!element.TryGetInt32(value: out var value)) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be an integer");
        }

        return value;
    }
    private static long RequireInt64(Dictionary<string, JsonElement> members, string name, string context) {
        var element = RequireNumberElement(
            context: context,
            expected: "an integer",
            members: members,
            name: name
        );

        if (!element.TryGetInt64(value: out var value)) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be an integer");
        }

        return value;
    }
    private static IntentSource RequireIntentSource(Dictionary<string, JsonElement> members, string context) {
        var token = RequireString(
            context: context,
            members: members,
            name: "source"
        );

        if (string.Equals(
            a: token,
            b: "live",
            comparisonType: StringComparison.Ordinal
        )) {
            return IntentSource.Live;
        }
        if (string.Equals(
            a: token,
            b: "idle",
            comparisonType: StringComparison.Ordinal
        )) {
            return IntentSource.Idle;
        }
        if (
            token.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "producer:"
        ) &&
            (token.Length > "producer:".Length)
        ) {
            return IntentSource.Producer(name: token["producer:".Length..]);
        }

        throw new AddonMutationDecodeException(message: $"{context}: 'source' must be live, idle, or producer:<name>");
    }
    // Unknown members Disallow, mirroring the world document's own ambient JsonUnmappedMemberHandling.Disallow
    // convention: a payload misspelling a key gets a loud refusal, never a silently ignored field.
    private static void RequireNoUnknownMembers(Dictionary<string, JsonElement> members, IReadOnlyList<string> allowed, string context) {
        foreach (var name in members.Keys) {
            var known = false;

            foreach (var candidate in allowed) {
                if (string.Equals(
                    a: candidate,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    known = true;
                    break;
                }
            }

            if (!known) {
                throw new AddonMutationDecodeException(message: $"{context}: unknown member '{name}'");
            }
        }
    }
    /// <summary>Owns lookup and JSON-kind validation for required numeric mutation members.</summary>
    private static JsonElement RequireNumberElement(Dictionary<string, JsonElement> members, string name, string context, string expected) {
        if (
            !members.TryGetValue(
            key: name,
            value: out var element
        ) ||
            (element.ValueKind != JsonValueKind.Number)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be {expected}");
        }

        return element;
    }
    private static WorldHudRect RequireRect(Dictionary<string, JsonElement> members, string name, string context) {
        if (
            !members.TryGetValue(
            key: name,
            value: out var rectElement
        ) ||
            (rectElement.ValueKind != JsonValueKind.Object)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be an object");
        }

        var rectMembers = UniqueMembers(
            context: $"{context}.{name}",
            element: rectElement
        );

        RequireNoUnknownMembers(
            allowed: RectMembers,
            context: $"{context}.{name}",
            members: rectMembers
        );

        var x = RequireFinite(
            context: $"{context}.{name}",
            members: rectMembers,
            name: "x"
        );
        var y = RequireFinite(
            context: $"{context}.{name}",
            members: rectMembers,
            name: "y"
        );
        var width = RequireFinite(
            context: $"{context}.{name}",
            members: rectMembers,
            name: "width"
        );
        var height = RequireFinite(
            context: $"{context}.{name}",
            members: rectMembers,
            name: "height"
        );

        if (width <= 0f) {
            throw new AddonMutationDecodeException(message: $"{context}.{name}: 'width' must be positive (got {width.ToString(provider: CultureInfo.InvariantCulture)})");
        }

        if (height <= 0f) {
            throw new AddonMutationDecodeException(message: $"{context}.{name}: 'height' must be positive (got {height.ToString(provider: CultureInfo.InvariantCulture)})");
        }

        return new WorldHudRect(
            Height: height,
            Width: width,
            X: x,
            Y: y
        );
    }
    private static long RequireStateNumber(JsonElement element, CellKind kind, string context) {
        if (kind == CellKind.Fixed) {
            if (
                (element.ValueKind != JsonValueKind.String) ||
                !Puck.Maths.FixedQ4816.TryParse(
                s: element.GetString(),
                provider: CultureInfo.InvariantCulture,
                result: out var parsed
            )
            ) {
                throw new AddonMutationDecodeException(message: $"{context}: must be a decimal string parseable as FixedQ4816 (e.g. \"12.5\"), never raw bits");
            }

            return parsed.Value;
        }

        if (
            (element.ValueKind != JsonValueKind.Number) ||
            !element.TryGetInt64(value: out var whole)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: must be a whole number");
        }

        return whole;
    }
    private static string RequireString(Dictionary<string, JsonElement> members, string name, string context) {
        if (
            !members.TryGetValue(
            key: name,
            value: out var element
        ) ||
            (element.ValueKind != JsonValueKind.String)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be a string");
        }

        return (element.GetString() ?? throw new AddonMutationDecodeException(message: $"{context}: '{name}' must not be null"));
    }
    private static uint RequireUInt32(Dictionary<string, JsonElement> members, string name, string context) {
        var element = RequireNumberElement(
            context: context,
            expected: "a non-negative integer",
            members: members,
            name: name
        );

        if (!element.TryGetUInt32(value: out var value)) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be a non-negative integer");
        }

        return value;
    }
    private static Vector3 RequireVector3(Dictionary<string, JsonElement> members, string name, string context) {
        if (
            !members.TryGetValue(
            key: name,
            value: out var element
        ) ||
            (element.ValueKind != JsonValueKind.Array)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be a [x, y, z] array");
        }

        var values = new float[3];
        var index = 0;

        foreach (var item in element.EnumerateArray()) {
            if (index >= 3) {
                throw new AddonMutationDecodeException(message: $"{context}: '{name}' must contain exactly 3 elements");
            }

            if (
                (item.ValueKind != JsonValueKind.Number) ||
                !item.TryGetSingle(value: out var component) ||
                !float.IsFinite(f: component)
            ) {
                throw new AddonMutationDecodeException(message: $"{context}: '{name}[{index}]' must be a finite number");
            }

            values[index] = component;
            index++;
        }

        if (index != 3) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must contain exactly 3 elements");
        }

        return new Vector3(
            x: values[0],
            y: values[1],
            z: values[2]
        );
    }
    // JsonDocument preserves every occurrence of a repeated name (unlike a source-gen POCO path, which silently
    // keeps the LAST one) — walking EnumerateObject ourselves is what lets a repeat be refused instead of silently
    // resolved to whichever occurrence a serializer would have picked. Mirrors SdfDocumentDecoder's own helper.
    private static Dictionary<string, JsonElement> UniqueMembers(JsonElement element, string context) {
        var members = new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject()) {
            if (!members.TryAdd(
                key: property.Name,
                value: property.Value
            )) {
                throw new AddonMutationDecodeException(message: $"{context}: duplicate key '{property.Name}'");
            }
        }

        return members;
    }

    /// <summary>Decodes one host-copied payload into a typed mutation, per the wire-declared kind ordinal a grant's
    /// verb mask already cleared it against (stages 1-5 own everything before this call). Never throws on
    /// guest-controlled input — every failure returns <see langword="false"/> with a human-readable reason.</summary>
    /// <param name="kindOrdinal">The wire-declared <see cref="MutationKindAttribute.Ordinal"/> the act named.</param>
    /// <param name="section">The section the deciding grant row's mask was checked against — a defense-in-depth
    /// cross-check that the decoded kind's own declared section agrees, never trusted from the wire alone.</param>
    /// <param name="payload">The host-copied payload bytes (UTF-8 JSON) — <see cref="ReadOnlyMemory{T}"/>, never a
    /// <see cref="Span{T}"/>, because <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/> is
    /// the overload that accepts one without an intermediate array copy.</param>
    /// <param name="principal">The acting identity to stamp onto the decoded mutation.</param>
    /// <param name="mutation">The decoded mutation, on success.</param>
    /// <param name="error">The human-readable refusal reason, on failure.</param>
    /// <returns><see langword="true"/> when the payload decoded to a well-formed mutation of the declared kind.</returns>
    public static bool TryDecode(int kindOrdinal, WorldSection section, ReadOnlyMemory<byte> payload, WorldPrincipal principal, out WorldMutation? mutation, out string error) {
        mutation = null;
        error = "";

        JsonDocument document;

        try {
            document = JsonDocument.Parse(utf8Json: payload);
        } catch (JsonException exception) {
            error = $"invalid JSON — {exception.Message}";
            return false;
        }

        using (document) {
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                error = "payload root must be a JSON object";
                return false;
            }

            try {
                mutation = kindOrdinal switch {
                    19 => DecodeUpsertPlacement(
                    root: document.RootElement,
                    principal: principal
                ),
                    20 => DecodeRemovePlacement(
                    root: document.RootElement,
                    principal: principal
                ),
                    41 => DecodeUpsertHudPanel(
                    root: document.RootElement,
                    principal: principal
                ),
                    42 => DecodeRemoveHudPanel(
                    root: document.RootElement,
                    principal: principal
                ),
                    43 => DecodeUpsertHudElement(
                    root: document.RootElement,
                    principal: principal
                ),
                    44 => DecodeRemoveHudElement(
                    root: document.RootElement,
                    principal: principal
                ),
                    45 => DecodeSetHudDefaults(
                    root: document.RootElement,
                    principal: principal
                ),
                    46 => DecodeUpsertStateRow(
                    root: document.RootElement,
                    principal: principal
                ),
                    47 => DecodeRemoveStateRow(
                    root: document.RootElement,
                    principal: principal
                ),
                    48 => DecodeSetInputHold(
                    root: document.RootElement,
                    principal: principal
                ),
                    _ => throw new AddonMutationDecodeException(message: $"kind ordinal {kindOrdinal} has no decoder wired"),
                };
            } catch (AddonMutationDecodeException exception) {
                error = exception.Message;
                return false;
            }

            // Defense in depth: the section the grant door bounded the mask against must be what the decoded kind's
            // OWN catalog entry declares — a mismatch here means the mask/kind pairing at the grant door and this
            // decoder's own ordinal table have drifted, never something a guest can trigger through the wire alone
            // (the kind switch above is total over its wired ordinals, each pinned to exactly one section below),
            // but checked rather than assumed.
            var expectedSection = kindOrdinal switch {
                19 or 20 => WorldSection.Placements,
                41 or 42 or 43 or 44 or 45 => WorldSection.Hud,
                46 or 47 => WorldSection.State,
                48 => WorldSection.InputHold,
                _ => section, // Unreachable: the switch above already refused any other ordinal.
            };

            if (section != expectedSection) {
                mutation = null;
                error = $"kind ordinal {kindOrdinal} decodes under section:{expectedSection.ToString().ToLowerInvariant()}, but the deciding grant was over section:{section.ToString().ToLowerInvariant()}";
                return false;
            }

            return true;
        }
    }

    // Internal control-flow exception, never allowed to escape TryDecode — every throw site above is guest-shaped
    // input the decoder itself refuses, caught once at the top and turned into the ordinary bool+out-error shape
    // every other stage of the dispatch door already uses.
    private sealed class AddonMutationDecodeException(string message) : Exception(message: message);
}
