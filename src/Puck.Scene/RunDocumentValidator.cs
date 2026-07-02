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

        if (!string.Equals(document.Version, PuckRunDocument.CurrentVersion, StringComparison.Ordinal)) {
            errors.Add(path: "version", message: $"expected \"{PuckRunDocument.CurrentVersion}\" but found \"{document.Version}\"");
        }

        ValidateExtensions(errors: errors, extensions: document.Extensions);
        document.Host?.Validate(errors: errors, path: "host");

        // fuzzing.bounds (if any) IS the shared envelope — it gates the scene validator too, not just the fuzzer.
        var effectiveBounds = (document.Fuzzing?.Bounds?.ToShapeBounds() ?? bounds);

        // A run declares exactly ONE root intent: a graph (live render), a validation gate, or a fuzzing run.
        var intents = ((document.Graph is not null) ? 1 : 0) + ((document.Validation is not null) ? 1 : 0) + ((document.Fuzzing is not null) ? 1 : 0);

        if (intents == 0) {
            errors.Add(path: "graph", message: "a run requires a graph node, a validation section, or a fuzzing section");
        } else if (intents > 1) {
            errors.Add(path: "graph", message: "graph, validation, and fuzzing are mutually exclusive — a run has exactly one root intent");
        }

        // Only some run shapes CONSUME the scene + viewports: the compute/ray-query world graphs and the data-driven
        // 'world' validation gate. The showcase graph renders its own built-in scene, the self-contained gates
        // (parity/export/compute/reverse) use none, and a fuzzing run generates its own — so a scene is not required for
        // those (but is still validated if present).
        var consumesScene = (document.Graph is WorldNode or RtNode)
            || ((document.Validation is ValidationDocument sceneGate) && string.Equals(sceneGate.Gate, "world", StringComparison.OrdinalIgnoreCase));

        ValidateScene(bounds: effectiveBounds, errors: errors, required: consumesScene, scene: (document.Scene ?? new SceneDocument()));
        ValidateViewports(errors: errors, required: consumesScene, viewports: (document.Viewports ?? []));

        if (document.Graph is not null) {
            document.Graph.Validate(errors: errors, path: "graph", viewportCount: (document.Viewports?.Count ?? 0));
        }

        document.Validation?.Validate(errors: errors, path: "validation", viewportCount: (document.Viewports?.Count ?? 0));
        document.Fuzzing?.Validate(errors: errors, path: "fuzzing");
        ValidateHostGraphConsistency(errors: errors, graph: document.Graph, host: document.Host);

        // A validation/fuzzing gate renders OFFSCREEN and LUID-matches a Direct3D 12 device from a Vulkan host, so a
        // directx host backend is meaningless here; reject it rather than silently overriding it to Vulkan.
        if (((document.Validation is not null) || (document.Fuzzing is not null)) && (document.Host?.Backend is string offscreenBackend) && string.Equals(offscreenBackend, "directx", StringComparison.OrdinalIgnoreCase)) {
            errors.Add(path: "host.backend", message: "host.backend:\"directx\" is incompatible with a validation/fuzzing run; the gate renders offscreen on a Vulkan host — omit host.backend or set it to \"vulkan\"");
        }

        return errors;
    }

    // Cross-section check: a Direct3D 12 host renders the world either same-device (no produce) or — with
    // produce:"vulkan" — via a bespoke Vulkan producer whose content the D3D12 host imports zero-copy (the reverse
    // cross-backend live path). That combination IS valid for the world graph. It is rejected only for the SHOWCASE,
    // which has no reverse cross-backend path (a D3D12 showcase host yields a blank window). The rt node has no
    // produce (it is rejected outright by RtNode.Validate), so it is exempt here.
    private static void ValidateHostGraphConsistency(NodeDocument? graph, HostDocument? host, ValidationErrors errors) {
        if ((host?.Backend is not string backend) || !string.Equals(backend, "directx", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        if ((graph is ShowcaseNode) && (graph.Produce is string produce) && string.Equals(produce, "vulkan", StringComparison.OrdinalIgnoreCase)) {
            errors.Add(path: "graph.produce", message: "produce:\"vulkan\" is incompatible with host.backend:\"directx\" on a showcase (there is no reverse cross-backend showcase); omit produce, set host.backend:\"vulkan\", or use the 'world' graph for the reverse cross-backend live path");
        }

        if (graph is CameraNode) {
            errors.Add(path: "host.backend", message: "host.backend:\"directx\" is incompatible with the 'camera' node; it produces on a bespoke Direct3D 12 device that only a Vulkan host can import zero-copy — omit host.backend or set it to \"vulkan\"");
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
                errors.Add(path: key, message: $"unknown top-level member '{key}'; top-level keys are case-sensitive camelCase (expected one of: version, host, scene, viewports, graph, validation, fuzzing)");
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

            if (sceneObject is not PlaneObject) {
                primitiveCount++;
            }

            sceneObject.Validate(bounds: bounds, errors: errors, materialCount: materialCount, path: $"scene.objects[{index}]");
        }

        if (required && (objects.Count == 0)) {
            errors.Add(path: "scene.objects", message: "at least one object is required");
        }

        if (primitiveCount > bounds.MaxPrimitives) {
            errors.Add(path: "scene.objects", message: $"places {primitiveCount} non-plane primitives; the limit is {bounds.MaxPrimitives}");
        }
    }
    private static void ValidateViewports(IReadOnlyList<Viewport> viewports, bool required, ValidationErrors errors) {
        if (required && (viewports.Count == 0)) {
            errors.Add(path: "viewports", message: "at least one viewport is required");
        }

        if (viewports.Count > 4) {
            errors.Add(path: "viewports", message: $"the compositor supports at most 4 viewports; found {viewports.Count}");
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
