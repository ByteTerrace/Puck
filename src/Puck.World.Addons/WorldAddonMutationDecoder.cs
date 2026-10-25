using Puck.Commands;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using Puck.World.Protocol;

namespace Puck.World.Addons;

/// <summary>
/// The addon mutation seam's stage 6 decoder (see <see cref="WorldAddonRuntime"/>'s <c>ResolveMutations</c>): turns
/// one guest-submitted JSON payload — already copied host-side out of guest linear memory by stage 5's
/// pointer-safety gate — into a typed <see cref="WorldMutation"/>. Hand-walked <see cref="JsonDocument"/>, the same
/// discipline <c>Puck.World.Client.Sdf.SdfDocumentDecoder</c> uses for its own untrusted-shaped door: every object's
/// members are collected once (a repeated key refuses, never silently resolves to whichever <c>JsonSerializer</c>
/// would have picked), checked against a per-kind allowed-member list (an unrecognized key refuses by name, never
/// ignored), and every scalar is finite-and-signedness-checked before it is trusted. The one exception is a placement
/// face, which reads through the document's own contract with repeated keys refused, so the face shape has one
/// spelling on both doors.
/// </summary>
/// <remarks>Wires the 5 HUD kinds
/// (<see cref="WorldMutation.UpsertHudPanel"/>/<see cref="WorldMutation.RemoveHudPanel"/>/
/// <see cref="WorldMutation.UpsertHudElement"/>/<see cref="WorldMutation.RemoveHudElement"/>/
/// <see cref="WorldMutation.SetHudDefaults"/>) — the ones <c>wasm/puck-addon-hudbuilder</c> exercises — plus the
/// state rows and cells (<see cref="WorldMutation.UpsertStateRow"/>/<see cref="WorldMutation.RemoveStateRow"/>,
/// <see cref="WorldMutation.UpsertStateCell"/>/<see cref="WorldMutation.RemoveStateCell"/>)
/// and the placement rows (<see cref="WorldMutation.UpsertPlacement"/>/<see cref="WorldMutation.RemovePlacement"/>,
/// the full <see cref="WorldPlacement"/> wire shape the document validator accepts — every facet: transform, distribution,
/// mirror, emission, solid, inhabit, faceSources, region, attach), plus <see cref="WorldMutation.SetInputHold"/>. Other
/// declared kinds have no entry yet;
/// <see cref="TryDecode"/> refuses an unwired ordinal by name rather than guessing a shape for it, so wiring one in
/// is strictly additive — a new <c>Decoders</c> row, never a change to this method's own contract.</remarks>
public static class WorldAddonMutationDecoder {
    /// <summary>Initializes the mutation vocabulary and JSON contracts while mounting, before the tick loop runs.</summary>
    public static void Prepare() => _ = Decoders.Count;

    private static readonly int UpsertStateRowOrdinal = WorldMutationKindCatalog.All().Single(predicate: static entry => (entry.Type == typeof(WorldMutation.UpsertStateRow))).Ordinal;
    // The kinds a guest may submit, keyed by the catalog's declared ordinal and section, so the dispatch reads the one
    // ordinal the catalog declares rather than restating it.
    private static readonly Dictionary<int, (WorldSection Section, Func<JsonElement, Principal, WorldMutation> Decode)> Decoders = Wire(
        (typeof(WorldMutation.UpsertPlacement), static (root, principal) => DecodeUpsertPlacement(principal: principal, root: root)),
        (typeof(WorldMutation.RemovePlacement), static (root, principal) => DecodeRemovePlacement(principal: principal, root: root)),
        (typeof(WorldMutation.UpsertHudPanel), static (root, principal) => DecodeUpsertHudPanel(principal: principal, root: root)),
        (typeof(WorldMutation.RemoveHudPanel), static (root, principal) => DecodeRemoveHudPanel(principal: principal, root: root)),
        (typeof(WorldMutation.UpsertHudElement), static (root, principal) => DecodeUpsertHudElement(principal: principal, root: root)),
        (typeof(WorldMutation.RemoveHudElement), static (root, principal) => DecodeRemoveHudElement(principal: principal, root: root)),
        (typeof(WorldMutation.SetHudDefaults), static (root, principal) => DecodeSetHudDefaults(principal: principal, root: root)),
        (typeof(WorldMutation.UpsertStateRow), static (root, principal) => DecodeUpsertStateRow(principal: principal, root: root)),
        (typeof(WorldMutation.RemoveStateRow), static (root, principal) => DecodeRemoveStateRow(principal: principal, root: root)),
        (typeof(WorldMutation.UpsertStateCell), static (root, principal) => DecodeUpsertStateCell(principal: principal, root: root)),
        (typeof(WorldMutation.RemoveStateCell), static (root, principal) => DecodeRemoveStateCell(principal: principal, root: root)),
        (typeof(WorldMutation.SetInputHold), static (root, principal) => DecodeSetInputHold(principal: principal, root: root))
    );
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
    private static readonly string[] UpsertStateCellMembers = ["row", "key", "value", "kind", "text"];
    private static readonly string[] RemoveStateCellMembers = ["row", "key"];
    private static readonly string[] StateRowMembers = ["name", "kind", "value", "cells", "min", "max", "capacity", "overflow"];
    private static readonly string[] StateCellMembers = ["key", "value"];
    private static readonly Dictionary<string, CellKind> CellKinds = new(comparer: StringComparer.Ordinal) {
        ["int"] = CellKind.Int,
        ["fixed"] = CellKind.Fixed,
        ["bool"] = CellKind.Bool,
        ["text"] = CellKind.Text,
        ["vector"] = CellKind.Vector,
    };
    private static readonly Dictionary<string, StateOverflow> Overflows = new(comparer: StringComparer.Ordinal) {
        ["refuse"] = StateOverflow.Refuse,
        ["saturate"] = StateOverflow.Saturate,
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
    // A face is read through the document's own contract, so this door decodes exactly the face shape a document row
    // carries: every source arm, the portal and the filter. The copy refuses a repeated key, which the document path
    // would resolve to its last occurrence, so a repeat is refused here as it is everywhere else on this seam.
    private static readonly JsonTypeInfo<WorldPlacementFace> FaceContract = ((JsonTypeInfo<WorldPlacementFace>)new JsonSerializerOptions(options: WorldJsonContext.Default.Options) {
        AllowDuplicateProperties = false,
    }.GetTypeInfo(type: typeof(WorldPlacementFace)));
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

        try {
            return (element.Deserialize(jsonTypeInfo: FaceContract) ?? throw new AddonMutationDecodeException(message: $"{context}: must be an object"));
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            throw new AddonMutationDecodeException(message: $"{context}: {WorldJsonPayload.Reason(exception: exception)}");
        }
    }
    // Kit/look name resolution and population-wide bounds stay with the document validator. An addon-authored
    // count is always a literal — the cell-reference shape (Puck.World.WorldPlacementInhabitCount.Row) is not part
    // of this wire grammar.
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
            Count: new WorldPlacementInhabitCount(Literal: count),
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
    private static WorldMutation DecodeRemoveHudElement(JsonElement root, Principal principal) {
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
    private static WorldMutation DecodeRemoveHudPanel(JsonElement root, Principal principal) {
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
    private static WorldMutation DecodeRemovePlacement(JsonElement root, Principal principal) {
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
    private static WorldMutation DecodeUpsertStateCell(JsonElement root, Principal principal) {
        var context = "UpsertStateCell";
        var members = UniqueMembers(context: context, element: root);

        RequireNoUnknownMembers(allowed: UpsertStateCellMembers, context: context, members: members);
        var spelling = RequireString(context: context, members: members, name: "kind");
        var kind = spelling switch {
            "set" => WorldDocumentWriteKind.Set,
            "add" => WorldDocumentWriteKind.Add,
            _ => throw new AddonMutationDecodeException(message: $"{context}: kind must be 'set' or 'add'"),
        };
        var text = (members.ContainsKey(key: "text") ? RequireString(context: context, members: members, name: "text") : null);

        return new WorldMutation.UpsertStateCell(principal,
            RequireString(context: context, members: members, name: "row"), RequireString(context: context, members: members, name: "key"),
            RequireInt64(context: context, members: members, name: "value"), kind, Text: text);
    }
    private static WorldMutation DecodeRemoveStateCell(JsonElement root, Principal principal) {
        var context = "RemoveStateCell";
        var members = UniqueMembers(context: context, element: root);

        RequireNoUnknownMembers(allowed: RemoveStateCellMembers, context: context, members: members);
        return new WorldMutation.RemoveStateCell(principal, RequireString(context: context, members: members, name: "row"), RequireString(context: context, members: members, name: "key"));
    }
    private static WorldMutation DecodeRemoveStateRow(JsonElement root, Principal principal) {
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
    private static WorldMutation DecodeSetHudDefaults(JsonElement root, Principal principal) {
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
    private static WorldMutation DecodeSetInputHold(JsonElement root, Principal principal) {
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
            !members.TryGetValue(
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
        Principal principal,
        string context,
        IReadOnlyList<string> allowed,
        string keyName,
        Func<Principal, string, WorldMutation> create) {
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
    private static StateCell DecodeStateCell(CellName key, JsonElement element, CellKind kind, string context) =>
        new(
            Key: key,
            Value: DecodeCellValue(
                context: context,
                element: element,
                kind: kind
            )
        );
    // One wire payload as the one carrier the row's kind declares. Every kind has its own arm, so a kind added to
    // CellKind arrives here as a refusal naming it rather than falling into a numeric read that would misread it.
    private static CellValue DecodeCellValue(JsonElement element, CellKind kind, string context) {
        switch (kind) {
            case CellKind.Text:
                if (element.ValueKind != JsonValueKind.String) {
                    throw new AddonMutationDecodeException(message: $"{context}: must be a string");
                }

                return CellValue.Text(value: (element.GetString() ?? string.Empty));
            case CellKind.Bool:
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
                    throw new AddonMutationDecodeException(message: $"{context}: must be a boolean");
                }

                return CellValue.Bool(value: element.GetBoolean());
            case CellKind.Vector:
                throw new AddonMutationDecodeException(message: $"{context}: vector state cells cannot be written by addons");
            case CellKind.Int:
                return CellValue.Int(value: RequireStateNumber(
                    context: context,
                    element: element,
                    kind: kind
                ));
            case CellKind.Fixed:
                return CellValue.Fixed(rawBits: RequireStateNumber(
                    context: context,
                    element: element,
                    kind: kind
                ));
            default:
                throw new AddonMutationDecodeException(message: $"{context}: '{kind}' is not a cell kind an addon can write");
        }
    }
    private static IReadOnlyList<StateCell> DecodeStateCells(JsonElement element, CellKind kind, string context, IReadOnlyList<StateCell>? previous = null) {
        if (element.ValueKind != JsonValueKind.Array) {
            throw new AddonMutationDecodeException(message: $"{context}: must be an array of {{\"key\", \"value\"}} objects");
        }

        var count = element.GetArrayLength();
        ImmutableArray<StateCell>.Builder? cells = null;
        var index = 0;

        foreach (var entry in element.EnumerateArray()) {
            // Keep diagnostic paths cold. A successful cell needs neither a property dictionary nor a formatted
            // path per field; the original decoder below still diagnoses every malformed shape in the same order.
            var old = (((previous is not null) && (index < previous.Count)) ? previous[index] : null);

            if (TryDecodePlainStateCell(cell: out var decoded, entry: entry, kind: kind, previous: old)) {
                if ((cells is null) && !ReferenceEquals(objA: decoded, objB: old)) {
                    cells = ImmutableArray.CreateBuilder<StateCell>(initialCapacity: count);
                    for (var copied = 0; (copied < index); copied++) { cells.Add(item: previous![copied]); }
                }
                cells?.Add(item: decoded!);
                index++;
                continue;
            }
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

            if (cells is null) {
                cells = ImmutableArray.CreateBuilder<StateCell>(initialCapacity: count);
                for (var copied = 0; (copied < index); copied++) { cells.Add(item: previous![copied]); }
            }
            cells.Add(item: DecodeStateCell(
                context: $"{cellContext}.value",
                element: valueElement,
                key: cellKey,
                kind: kind
            ));
            index++;
        }

        if ((cells is null) && (previous is not null) && (previous.Count == count)) { return previous; }
        if (cells is null) {
            cells = ImmutableArray.CreateBuilder<StateCell>(initialCapacity: count);
            for (var copied = 0; (copied < count); copied++) { cells.Add(item: previous![copied]); }
        }
        return cells.MoveToImmutable();
    }
    private static bool TryDecodePlainStateCell(JsonElement entry, CellKind kind, out StateCell? cell, StateCell? previous = null) {
        cell = null;
        if (previous is not {
            Advance: null, Cycle: null, Dynamics: null, Clock: null, Provenance: null,
            Visibility: null, Observation: null, Behavior: StateCellBehavior.Inherit
        }) { previous = null; }
        if (entry.ValueKind != JsonValueKind.Object) { return false; }
        JsonElement key = default, value = default;

        foreach (var property in entry.EnumerateObject()) {
            if (property.NameEquals(utf8Text: "key"u8) && (key.ValueKind == JsonValueKind.Undefined)) { key = property.Value; } else if (property.NameEquals(utf8Text: "value"u8) && (value.ValueKind == JsonValueKind.Undefined)) { value = property.Value; } else { return false; }
        }
        if ((key.ValueKind != JsonValueKind.String) || (value.ValueKind == JsonValueKind.Undefined)) { return false; }
        CellName name;

        if ((previous is not null) && key.ValueEquals(text: previous.Key.Value)) { name = previous.Key; } else if (!CellName.TryParse(candidate: key.GetString(), name: out name, reason: out _)) { return false; }
        try {
            var decoded = (((kind == CellKind.Text) && (previous?.Value.Kind == CellKind.Text) &&
                (value.ValueKind == JsonValueKind.String) && value.ValueEquals(text: previous.Value.AsText))
                ? previous.Value : DecodeCellValue(context: "value", element: value, kind: kind));

            cell = (((previous is not null) && (previous.Key == name) && (previous.Value == decoded))
                ? previous : new StateCell(name, decoded));
            return true;
        } catch (AddonMutationDecodeException) {
            return false;
        }
    }
    private static WorldMutation DecodeUpsertHudElement(JsonElement root, Principal principal) {
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
    private static WorldMutation DecodeUpsertHudPanel(JsonElement root, Principal principal) {
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

    private static WorldMutation DecodeUpsertPlacement(JsonElement root, Principal principal) {
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
    // list, one walk, no per-$type branching. Min/Max ordering, the value's own in-range check, and the
    // capacity/text-length ceilings are all WorldDefinitionValidator's job; this only turns wire scalars into the
    // typed row, exactly like the HUD decoders leave capacity/authoring-policy checks to the same validate stage.
    private static WorldMutation DecodeUpsertStateRow(JsonElement root, Principal principal, WorldMutation.UpsertStateRow? prior = null) {
        const string Context = "UpsertStateRow";
        var previous = prior?.Row;

        var members = UniqueMembers(
            context: Context,
            element: root
        );

        RequireNoUnknownMembers(
            allowed: StateRowMembers,
            context: Context,
            members: members
        );

        var name = (((previous is not null) && members.TryGetValue(key: "name", value: out var nameElement) &&
            (nameElement.ValueKind == JsonValueKind.String) && nameElement.ValueEquals(text: previous.Name.Value))
            ? previous.Name.Value : RequireString(
            context: Context,
            members: members,
            name: "name"
        ));
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

        var row = new WorldStateRow(
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
                Overflow: (members.TryGetValue(
                    key: "overflow",
                    value: out var overflowElement
                )
            ? (Overflows.TryGetValue(
                    key: ((overflowElement.ValueKind == JsonValueKind.String)
                        ? (overflowElement.GetString() ?? "")
                        : throw new AddonMutationDecodeException(message: $"{Context}: 'overflow' must be a string")),
                    value: out var overflow
                )
                ? overflow
                : throw new AddonMutationDecodeException(message: $"{Context}: 'overflow' names an unknown value — one of {{{string.Join(
                    separator: ", ",
                    values: Overflows.Keys
                )}}}"))
            : StateOverflow.Refuse),
                Cells: (hasValue
            ? [DecodeStateCell(
                            context: $"{Context}.value",
                            element: valueElement,
                            key: WorldStateRow.SlotKey,
                            kind: kind
                        )]
            : (hasCells
                ? DecodeStateCells(
                            previous: (((previous is not null) && (previous.Name == rowName) && (previous.Kind == kind)) ? previous.Cells : null),
                            context: $"{Context}.cells",
                            element: cellsElement,
                            kind: kind
                        )
                : []))

        );

        return (((prior is not null) && (prior.Principal == principal) && (row == previous)) ? prior : new WorldMutation.UpsertStateRow(Principal: principal, Row: row));
    }
    private static long? OptionalInt64(JsonMembers members, string name, string context) {
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
    private static long? OptionalStateNumber(JsonMembers members, string name, CellKind kind, string context) =>
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
    private static bool RequireBool(JsonMembers members, string name, string context) {
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
    private static TEnum RequireEnum<TEnum>(JsonMembers members, string name, Dictionary<string, TEnum> map, string context) where TEnum : struct, Enum {
        if (
            !members.TryGetValue(
            key: name,
            value: out var element
        ) ||
            (element.ValueKind != JsonValueKind.String)
        ) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be a string");
        }

        foreach (var (token, value) in map) {
            if (element.ValueEquals(text: token)) { return value; }
        }
        throw new AddonMutationDecodeException(message: $"{context}: '{name}' names '{(element.GetString() ?? "")}', which is not one of {{{string.Join(
                separator: ", ",
                values: map.Keys
            )}}}");
    }
    private static float RequireFinite(JsonMembers members, string name, string context) {
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

    private delegate bool TryGetNumber<T>(JsonElement element, out T value);

    private static T RequireNumber<T>(JsonMembers members, string name, string context, string expected, TryGetNumber<T> getter) {
        var element = RequireNumberElement(
            context: context,
            expected: expected,
            members: members,
            name: name
        );

        if (!getter(element, out var value)) {
            throw new AddonMutationDecodeException(message: $"{context}: '{name}' must be {expected}");
        }

        return value;
    }
    private static int RequireInt32(JsonMembers members, string name, string context) =>
        RequireNumber(
            context: context,
            expected: "an integer",
            getter: static (JsonElement e, out int val) => e.TryGetInt32(value: out val),
            members: members,
            name: name
        );
    private static long RequireInt64(JsonMembers members, string name, string context) =>
        RequireNumber(
            context: context,
            expected: "an integer",
            getter: static (JsonElement e, out long val) => e.TryGetInt64(value: out val),
            members: members,
            name: name
        );
    private static IntentSource RequireIntentSource(JsonMembers members, string context) {
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
    private static void RequireNoUnknownMembers(JsonMembers members, IReadOnlyList<string> allowed, string context) {
        foreach (var property in members.Element.EnumerateObject()) {
            var known = false;

            for (var index = 0; (index < allowed.Count); index++) {
                if (property.NameEquals(text: allowed[index])) {
                    known = true;
                    break;
                }
            }

            if (!known) {
                throw new AddonMutationDecodeException(message: $"{context}: unknown member '{property.Name}'");
            }
        }
    }
    /// <summary>Owns lookup and JSON-kind validation for required numeric mutation members.</summary>
    private static JsonElement RequireNumberElement(JsonMembers members, string name, string context, string expected) {
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
    private static WorldHudRect RequireRect(JsonMembers members, string name, string context) {
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
    private static string RequireString(JsonMembers members, string name, string context) {
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
    private static Vector3 RequireVector3(JsonMembers members, string name, string context) {
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

    private readonly struct JsonMembers(JsonElement element) {
        public JsonElement Element { get; } = element;

        public bool TryGetValue(string key, out JsonElement value) => Element.TryGetProperty(propertyName: key, value: out value);
        public bool ContainsKey(string key) => Element.TryGetProperty(propertyName: key, value: out _);
    }

    // The object remains in the document's pooled storage until decode ends. Recognized property names need no
    // strings or dictionary: each declared name has a bit. Unknown names take the diagnostic walk, which still
    // diagnoses the first duplicate before unknown-member validation, including duplicate unknown names.
    private static readonly string[] MemberNames = new[] {
        RectMembers, ElementMembers, PanelMembers, RemovePanelMembers, RemoveElementMembers, SetDefaultsMembers,
        SetDefaultsCursorMembers, RemoveStateRowMembers, UpsertStateCellMembers, RemoveStateCellMembers,
        StateRowMembers, StateCellMembers, InputHoldMembers, InputHoldParticipantMembers, PlacementMembers,
        RemovePlacementMembers, DistributionMembers, DiscRegionMembers, PointsRegionMembers, LatticeRegionMembers,
        SequenceMembers, MirrorMembers, EmissionMembers, SolidMembers, RegionMembers, AttachMembers, InhabitMembers,
    }.SelectMany(selector: static names => names).Distinct(comparer: StringComparer.Ordinal).ToArray();

    private static JsonMembers UniqueMembers(JsonElement element, string context) {
        Span<ulong> seen = stackalloc ulong[((MemberNames.Length + 63) / 64)];

        seen.Clear();

        foreach (var property in element.EnumerateObject()) {
            var index = 0;

            while ((index < MemberNames.Length) && !property.NameEquals(text: MemberNames[index])) { index++; }
            if (index == MemberNames.Length) {
                var names = new HashSet<string>(comparer: StringComparer.Ordinal);

                foreach (var entry in element.EnumerateObject()) {
                    if (!names.Add(item: entry.Name)) {
                        throw new AddonMutationDecodeException(message: $"{context}: duplicate key '{entry.Name}'");
                    }
                }
                return new JsonMembers(element: element);
            }
            var bit = (1UL << (index & 63));

            if ((seen[(index >> 6)] & bit) != 0UL) {
                throw new AddonMutationDecodeException(message: $"{context}: duplicate key '{property.Name}'");
            }
            seen[(index >> 6)] |= bit;
        }

        return new JsonMembers(element: element);
    }
    // Keys each wired kind's decoder by the ordinal and section its catalog entry declares.
    private static Dictionary<int, (WorldSection Section, Func<JsonElement, Principal, WorldMutation> Decode)> Wire(params (Type Kind, Func<JsonElement, Principal, WorldMutation> Decode)[] kinds) {
        var catalog = WorldMutationKindCatalog.All();
        var decoders = new Dictionary<int, (WorldSection Section, Func<JsonElement, Principal, WorldMutation> Decode)>(capacity: kinds.Length);

        foreach (var (kind, decode) in kinds) {
            var entry = catalog.Single(predicate: candidate => (candidate.Type == kind));

            decoders.Add(key: entry.Ordinal, value: (entry.Section, decode));
        }

        return decoders;
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
    /// <param name="previous">A prior immutable decode from this guest slot, whose unchanged cells may be shared.</param>
    /// <returns><see langword="true"/> when the payload decoded to a well-formed mutation of the declared kind.</returns>
    public static bool TryDecode(int kindOrdinal, WorldSection section, ReadOnlyMemory<byte> payload, Principal principal, out WorldMutation? mutation, out string error, WorldMutation? previous = null) {
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

            if (!Decoders.TryGetValue(key: kindOrdinal, value: out var wired)) {
                error = $"kind ordinal {kindOrdinal} has no decoder wired";
                return false;
            }

            try {
                mutation = (((previous is WorldMutation.UpsertStateRow prior) &&
                    (kindOrdinal == UpsertStateRowOrdinal))
                    ? DecodeUpsertStateRow(document.RootElement, principal, prior)
                    : wired.Decode(arg1: document.RootElement, arg2: principal));
            } catch (AddonMutationDecodeException exception) {
                error = exception.Message;
                return false;
            }

            // Defense in depth: the section the grant door bounded the mask against must be what the decoded kind's
            // own catalog entry declares, checked rather than assumed.
            var expectedSection = wired.Section;

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
