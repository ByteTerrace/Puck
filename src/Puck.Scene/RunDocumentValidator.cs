using System.Text.Json;

namespace Puck.Scene;

/// <summary>
/// The thick parse-time gate: every semantic invariant the GPU and the builders assume is asserted HERE, before a
/// single word reaches a shader. Composed of small per-section checks (version, materials, objects, viewports, graph),
/// it collects ALL failures in one pass and reports them with source-attributed paths. A document that survives
/// validation is guaranteed buildable — the builders never re-check what the validator already proved.
/// </summary>
public static class RunDocumentValidator {
    private static readonly FloatRange s_albedoChannel = new(Maximum: 1f, Minimum: 0f);
    private static readonly FloatRange s_emissive = new(Maximum: 8f, Minimum: 0f);
    private static readonly FloatRange s_shininess = new(Maximum: 256f, Minimum: 1f);
    private static readonly FloatRange s_specular = new(Maximum: 1f, Minimum: 0f);

    /// <summary>Validates a document, throwing if it is not buildable.</summary>
    /// <param name="document">The deserialized document.</param>
    /// <param name="bounds">The renderability envelope to validate scene parameters against; defaults to <see cref="ShapeBounds.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="RunDocumentValidationException">The document failed one or more invariants.</exception>
    public static void Validate(PuckRunDocument document, ShapeBounds? bounds = null) {
        var errors = Collect(bounds: (bounds ?? ShapeBounds.Default), document: document);

        if (errors.HasErrors) {
            throw new RunDocumentValidationException(errors: errors.Messages);
        }
    }

    private static ValidationErrors Collect(PuckRunDocument document, ShapeBounds bounds) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var errors = new ValidationErrors();

        if (!string.Equals(a: document.Version, b: PuckRunDocument.CurrentVersion, comparisonType: StringComparison.Ordinal)) {
            errors.Add(path: "version", message: $"expected \"{PuckRunDocument.CurrentVersion}\" but found \"{document.Version}\"");
        }

        ValidateExtensions(errors: errors, extensions: document.Extensions);
        document.Host?.Validate(errors: errors, path: "host");

        // fuzzing.bounds (if any) IS the shared envelope — it gates the scene validator too, not just the fuzzer.
        var effectiveBounds = (document.Fuzzing?.Bounds?.ToShapeBounds() ?? bounds);

        // A run declares exactly ONE root intent: a graph (live render), a validation gate, or a fuzzing run.
        var intents = ((((document.Graph is not null) ? 1 : 0) + ((document.Validation is not null) ? 1 : 0)) + ((document.Fuzzing is not null) ? 1 : 0));

        if (intents == 0) {
            errors.Add(path: "graph", message: "a run requires a graph node, a validation section, or a fuzzing section");
        } else if (intents > 1) {
            errors.Add(path: "graph", message: "graph, validation, and fuzzing are mutually exclusive — a run has exactly one root intent");
        }

        // Only some run shapes CONSUME the scene + viewports: the compute world graph and the data-driven 'world'
        // validation gate. Overworld builds its own dynamic scene, the self-contained gates use none, and a fuzzing
        // run generates its own — so a scene is not required for those (but is still validated if present).
        var consumesScene = ((document.Graph is WorldNode)
            || ((document.Validation is ValidationDocument sceneGate) && string.Equals(a: sceneGate.Gate, b: "world", comparisonType: StringComparison.OrdinalIgnoreCase)));

        ValidateScene(bounds: effectiveBounds, errors: errors, required: consumesScene, scene: (document.Scene ?? new SceneDocument()));
        ValidateViewports(errors: errors, required: consumesScene, viewports: (document.Viewports ?? []));
        ValidateScreenSources(consumed: (document.Graph is WorldNode), errors: errors, screenSources: document.ScreenSources, viewports: (document.Viewports ?? []));
        ValidateAddons(addons: document.Addons, consumed: (document.Graph is not null), errors: errors);

        if (document.Graph is not null) {
            document.Graph.Validate(errors: errors, path: "graph", viewportCount: (document.Viewports?.Count ?? 0));
        }

        document.Validation?.Validate(errors: errors, path: "validation", viewportCount: (document.Viewports?.Count ?? 0));
        document.Fuzzing?.Validate(errors: errors, path: "fuzzing");
        document.Input?.Validate(errors: errors, path: "input");

        // A validation/fuzzing gate renders OFFSCREEN and LUID-matches a Direct3D 12 device from a Vulkan host, so a
        // directx host backend is meaningless here; reject it rather than silently overriding it to Vulkan.
        if (((document.Validation is not null) || (document.Fuzzing is not null)) && (document.Host?.Backend is string offscreenBackend) && string.Equals(a: offscreenBackend, b: "directx", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            errors.Add(path: "host.backend", message: "host.backend:\"directx\" is incompatible with a validation/fuzzing run; the gate renders offscreen on a Vulkan host — omit host.backend or set it to \"vulkan\"");
        }

        return errors;
    }

    // Mirrors ValidateScreenSources: a top-level list section, null-tolerant, index-attributed paths, rejected when
    // the selected root intent would silently ignore it (here, any graph run consumes addons; validation/fuzzing do
    // not), plus a cross-entry duplicate-identity check (name, and separately the declared exclusive slot — two
    // addons cannot own one roster slot, the isolation unit the demo's ghost lifecycle depends on). Null slots are
    // not deduped here: the host seats them at the first free non-human slot, so defaults never collide.
    private static void ValidateAddons(IReadOnlyList<AddonDocument>? addons, bool consumed, ValidationErrors errors) {
        if (addons is null) {
            return;
        }

        if (!consumed && (addons.Count > 0)) {
            errors.Add(path: "addons", message: "addons is only consumed by a graph run; this run's root intent would silently ignore it");
        }

        var seenNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var seenSlots = new HashSet<int>();

        for (var index = 0; (index < addons.Count); index++) {
            var entry = addons[index];
            var path = $"addons[{index}]";

            if (entry is null) {
                errors.Add(path: path, message: "an addon entry cannot be null");

                continue;
            }

            if (!string.IsNullOrWhiteSpace(value: entry.Name) && !seenNames.Add(item: entry.Name)) {
                errors.Add(path: $"{path}.name", message: $"addon name '{entry.Name}' is declared by more than one entry");
            }

            if ((entry.Slot is int slot) && !seenSlots.Add(item: slot)) {
                errors.Add(path: $"{path}.slot", message: $"slot {slot} is declared by more than one addon");
            }

            entry.Validate(errors: errors, path: path);
        }
    }

    // Unknown top-level members were captured into the root's [JsonExtensionData] rather than rejected (so a strict
    // Disallow could not run on the root). Forward-compat is carried by the `version` discriminator — a newer writer
    // that adds a section also bumps the version — so within a recognized version any unknown root key is an authoring
    // mistake (most often a mis-cased or mistyped section name), reported here at its source instead of silently
    // ignored. Keys beginning with '$' (e.g. "$schema") or '_' (comments) are reserved escape hatches and allowed.
    private static void ValidateExtensions(IDictionary<string, JsonElement>? extensions, ValidationErrors errors) {
        if (extensions is null) {
            return;
        }

        foreach (var key in extensions.Keys) {
            if ((key.Length != 0) && (key[0] != '$') && (key[0] != '_')) {
                errors.Add(path: key, message: $"unknown top-level member '{key}'; top-level keys are case-sensitive camelCase (expected one of: version, host, scene, viewports, screenSources, addons, graph, validation, fuzzing)");
            }
        }
    }
    private static void ValidateScene(SceneDocument scene, ShapeBounds bounds, bool required, ValidationErrors errors) {
        var materials = (scene.Materials ?? []);
        var objects = (scene.Objects ?? []);
        var materialCount = materials.Count;
        // A scene needs a material only if some object actually indexes the palette; a screen-slab-only scene uses
        // the screen-material sentinel and references none.
        var referencesPalette = objects.Any(predicate: static sceneObject => ((sceneObject is not null) && sceneObject.ReferencesMaterialPalette));

        if (referencesPalette && (materialCount == 0)) {
            errors.Add(path: "scene.materials", message: "at least one material is required (an object references the material palette)");
        }

        if (materialCount > bounds.MaxMaterials) {
            errors.Add(path: "scene.materials", message: $"declares {materialCount} materials; the limit is {bounds.MaxMaterials}");
        }

        for (var index = 0; (index < materialCount); index++) {
            var material = materials[index];

            if (material is null) {
                errors.Add(path: $"scene.materials[{index}]", message: "a material entry cannot be null");

                continue;
            }

            var albedo = material.Albedo;
            var path = $"scene.materials[{index}].albedo";

            errors.RequireRange(path: $"scene.materials[{index}].emissive", name: "emissive", range: s_emissive, value: material.Emissive);
            errors.RequireRange(path: $"scene.materials[{index}].specular", name: "specular", range: s_specular, value: material.Specular);

            if (material.Shininess is float shininess) {
                errors.RequireRange(path: $"scene.materials[{index}].shininess", name: "shininess", range: s_shininess, value: shininess);
            }

            if (!JsonVector.IsValid(components: albedo, length: 3)) {
                errors.RequireVector(path: path, components: albedo, length: 3);

                continue;
            }

            errors.RequireRange(path: $"{path}[0]", name: "r", range: s_albedoChannel, value: albedo[0]);
            errors.RequireRange(path: $"{path}[1]", name: "g", range: s_albedoChannel, value: albedo[1]);
            errors.RequireRange(path: $"{path}[2]", name: "b", range: s_albedoChannel, value: albedo[2]);
        }

        var primitiveCount = 0;

        for (var index = 0; (index < objects.Count); index++) {
            var sceneObject = objects[index];

            if (sceneObject is null) {
                errors.Add(path: $"scene.objects[{index}]", message: "an object entry cannot be null");

                continue;
            }

            primitiveCount += CountPrimitives(sceneObject: sceneObject);

            sceneObject.Validate(bounds: bounds, errors: errors, materialCount: materialCount, path: $"scene.objects[{index}]");
        }

        if (required && (objects.Count == 0)) {
            errors.Add(path: "scene.objects", message: "at least one object is required");
        }

        if (primitiveCount > bounds.MaxPrimitives) {
            errors.Add(path: "scene.objects", message: $"places {primitiveCount} non-plane primitives; the limit is {bounds.MaxPrimitives}");
        }
    }
    // How many non-plane primitives one top-level object contributes toward bounds.MaxPrimitives — a GROUP is not
    // itself a primitive, so it recurses into its members (real GPU/march cost lives there, not on the group
    // wrapper); a null member (an invalid document the per-member Validate call above already flags) contributes
    // nothing rather than throwing here. GroupObject nesting is validator-rejected, so this never recurses past one
    // level in a document that otherwise passes, but the recursion costs nothing to leave general.
    private static int CountPrimitives(SceneObject sceneObject) {
        if (sceneObject is GroupObject group) {
            return (group.Objects ?? []).Sum(selector: static member => ((member is null) ? 0 : CountPrimitives(sceneObject: member)));
        }

        return ((sceneObject is PlaneObject) ? 0 : 1);
    }
    private static void ValidateScreenSources(IReadOnlyList<ScreenSourceDocument>? screenSources, IReadOnlyList<Viewport> viewports, bool consumed, ValidationErrors errors) {
        if (screenSources is null) {
            return;
        }

        // The doctrine: the validator rejects data the selected root intent will not consume — a table that would
        // survive validation only to be silently ignored is an authoring mistake, reported at its source.
        if (!consumed && (screenSources.Count > 0)) {
            errors.Add(path: "screenSources", message: "screenSources is only consumed by the world graph; this run's root intent would silently ignore it");
        }

        var seenIndices = new HashSet<int>();

        for (var index = 0; (index < screenSources.Count); index++) {
            var entry = screenSources[index];
            var path = $"screenSources[{index}]";

            if (entry is null) {
                errors.Add(path: path, message: "a screen-source entry cannot be null");

                continue;
            }

            if (!seenIndices.Add(item: entry.ScreenIndex)) {
                errors.Add(path: $"{path}.screenIndex", message: $"screen index {entry.ScreenIndex} is fed by more than one entry");
            }

            entry.Validate(errors: errors, path: path, viewports: viewports);
        }
    }
    private static void ValidateViewports(IReadOnlyList<Viewport> viewports, bool required, ValidationErrors errors) {
        if (required && (viewports.Count == 0)) {
            errors.Add(path: "viewports", message: "at least one viewport is required");
        }

        if (viewports.Count > 5) {
            errors.Add(path: "viewports", message: $"the compositor supports at most 5 viewports; found {viewports.Count}");
        }

        for (var index = 0; (index < viewports.Count); index++) {
            var viewport = viewports[index];

            if (viewport is null) {
                errors.Add(path: $"viewports[{index}]", message: "a viewport entry cannot be null");

                continue;
            }

            viewport.Validate(errors: errors, path: $"viewports[{index}]");
        }
    }
}
