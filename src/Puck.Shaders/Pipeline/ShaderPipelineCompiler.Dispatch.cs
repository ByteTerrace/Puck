namespace Puck.Shaders;

// Dispatch shapes and package storage. A pass dispatches over the frame extent, by fixed group counts, or indirectly
// from a buffer version it reads in the indirect-argument state; a buffer is raw and fixed, or structured by an element
// stride, or counted by a basis the host resolves. The pipeline node records only extent dispatches over raw fixed
// buffers, so the other shapes and storages belong to package passes, which record their own work.
public sealed partial class ShaderPipelineCompiler {
    // Everything a pass reads this frame or the previous one: an indirect dispatch's arguments, then its inputs.
    private static IEnumerable<ResourceReference> ReadsOf(ShaderPipelinePass pass) {
        if (pass.DispatchArguments is { } arguments) {
            yield return new ResourceReference(Name: arguments);
        }
        foreach (var input in pass.InputReferences) {
            yield return input;
        }
    }
    private static void ValidateBufferLayout(ShaderPipelineResource resource, List<ShaderPipelineDiagnostic> diagnostics) {
        if (resource.Kind != ShaderPipelineResourceKind.Buffer) {
            if (resource.IsPackageStorage) {
                Add(
                    diagnostics,
                    "SHADERPIPE_RESOURCE_KIND_FIELDS",
                    $"Image/depth resource '{resource.Name}' cannot declare the buffer fields strideBytes or count.",
                    resource.Name
                );
            }

            return;
        }
        if (resource.StrideBytes is { } stride) {
            if (
                (stride == 0) ||
                ((stride & 3) != 0)
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_BUFFER_STRIDE",
                    $"Buffer resource '{resource.Name}' strideBytes must be a positive multiple of four.",
                    resource.Name
                );
            } else if (
                (resource.SizeBytes is { } size) &&
                ((size % stride) != 0)
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_BUFFER_STRIDE",
                    $"Buffer resource '{resource.Name}' sizeBytes {size} is not a whole number of {stride}-byte elements.",
                    resource.Name
                );
            }
        }
        if (resource.Count is not { } count) {
            return;
        }
        if (resource.SizeBytes is not null) {
            Add(
                diagnostics,
                "SHADERPIPE_BUFFER_SIZE",
                $"Buffer resource '{resource.Name}' declares both sizeBytes and a count; a buffer is fixed or counted.",
                resource.Name
            );
        }
        if (
            !Enum.IsDefined(value: count.Basis) ||
            (count.Elements == 0)
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_BUFFER_COUNT",
                $"Buffer resource '{resource.Name}' needs a declared count basis and at least one element per unit.",
                resource.Name
            );
        }
    }
    private static void ValidateDispatch(ShaderPipelinePass pass, bool package, IReadOnlyDictionary<string, ShaderPipelineResource> resources, List<ShaderPipelineDiagnostic> diagnostics) {
        if (pass.Dispatch is not { } dispatch) {
            return;
        }
        if (pass.IsGraphics) {
            Add(
                diagnostics,
                "SHADERPIPE_DISPATCH_SHAPE",
                $"Graphics pass '{pass.Name}' declares a dispatch; a graphics pass draws.",
                pass.Name
            );

            return;
        }
        if (!Enum.IsDefined(value: dispatch.Kind)) {
            Add(
                diagnostics,
                "SHADERPIPE_DISPATCH_SHAPE",
                $"Pass '{pass.Name}' has an unknown dispatch kind '{dispatch.Kind}'.",
                pass.Name
            );

            return;
        }

        var groups = (dispatch.Kind == ShaderPipelineDispatchKind.Groups);

        if (groups
            ? ((dispatch.GroupCountX == 0) || (dispatch.GroupCountY == 0) || (dispatch.GroupCountZ == 0))
            : ((dispatch.GroupCountX != 1) || (dispatch.GroupCountY != 1) || (dispatch.GroupCountZ != 1))
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_DISPATCH_SHAPE",
                (groups
                    ? $"Pass '{pass.Name}' dispatches {dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} groups; every group count is at least one."
                    : $"Pass '{pass.Name}' declares group counts on a {dispatch.Kind} dispatch; only a Groups dispatch declares them."),
                pass.Name
            );
        }
        if (dispatch.Kind != ShaderPipelineDispatchKind.Indirect) {
            if (
                (dispatch.Arguments is not null) ||
                (dispatch.ArgumentsOffsetBytes != 0)
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_DISPATCH_SHAPE",
                    $"Pass '{pass.Name}' names dispatch arguments on a {dispatch.Kind} dispatch; only an Indirect dispatch reads them.",
                    pass.Name
                );
            }
        } else {
            ValidateArguments(
                diagnostics: diagnostics,
                dispatch: dispatch,
                pass: pass,
                resources: resources
            );
        }
        if (
            !package &&
            (dispatch.Kind != ShaderPipelineDispatchKind.Extent)
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_DISPATCH_PACKAGE",
                $"Shader pass '{pass.Name}' declares a {dispatch.Kind} dispatch; the pipeline node dispatches a shader pass over its extent, and only a package pass records another shape.",
                pass.Name
            );
        }
    }
    private static void ValidateArguments(ShaderPipelinePass pass, ShaderPipelineDispatch dispatch, IReadOnlyDictionary<string, ShaderPipelineResource> resources, List<ShaderPipelineDiagnostic> diagnostics) {
        if (string.IsNullOrWhiteSpace(value: dispatch.Arguments)) {
            Add(
                diagnostics,
                "SHADERPIPE_DISPATCH_ARGUMENTS",
                $"Pass '{pass.Name}' dispatches indirectly without naming its arguments buffer.",
                pass.Name
            );

            return;
        }

        var name = dispatch.Arguments;

        if (!resources.TryGetValue(
            key: name,
            value: out var resource
        )) {
            Add(
                diagnostics,
                "SHADERPIPE_UNKNOWN_RESOURCE",
                $"Pass '{pass.Name}' reads undeclared dispatch arguments '{name}'.",
                name
            );

            return;
        }
        if (resource.Kind != ShaderPipelineResourceKind.Buffer) {
            Add(
                diagnostics,
                "SHADERPIPE_DISPATCH_ARGUMENTS",
                $"Pass '{pass.Name}' reads dispatch arguments from {resource.Kind} '{name}'; arguments are a buffer's words.",
                name
            );

            return;
        }
        if (
            ((dispatch.ArgumentsOffsetBytes & 3) != 0) ||
            (
                (resource.SizeBytes is { } size) &&
                ((dispatch.ArgumentsOffsetBytes > size) || ((size - dispatch.ArgumentsOffsetBytes) < ShaderPipelineDispatch.ArgumentBytes))
            )
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_DISPATCH_ARGUMENTS",
                $"Pass '{pass.Name}' reads its {ShaderPipelineDispatch.ArgumentBytes} argument bytes at offset {dispatch.ArgumentsOffsetBytes} of '{name}'; the offset is a multiple of four and the words lie inside the buffer.",
                name
            );
        }
        if (pass.InputReferences.Concat(second: pass.OutputReferences).Any(predicate: reference => string.Equals(
            a: reference.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ))) {
            Add(
                diagnostics,
                "SHADERPIPE_DISPATCH_ARGUMENTS",
                $"Pass '{pass.Name}' also binds its dispatch arguments '{name}'; a version a dispatch reads as arguments is in the indirect-argument state for the whole pass.",
                name
            );
        }
    }
    // A structured or counted buffer is a package's storage: a shader pass binding it, or a publication of it, would
    // reach the pipeline node, which allocates and binds raw buffers of fixed size. A package's own passes may bind
    // them, so the caller checks only the document's passes.
    private static void ValidatePackageStorage(ShaderPipelinePass pass, IReadOnlyDictionary<string, ShaderPipelineResource> resources, List<ShaderPipelineDiagnostic> diagnostics) {
        foreach (var reference in ReadsOf(pass: pass).Concat(second: pass.OutputReferences)) {
            if (
                resources.TryGetValue(
                    key: reference.Name,
                    value: out var resource
                ) &&
                (resource.Kind == ShaderPipelineResourceKind.Buffer) &&
                resource.IsPackageStorage
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_PACKAGE_STORAGE",
                    $"Shader pass '{pass.Name}' binds '{reference.Name}', which declares a stride or a count; the pipeline node binds raw buffers of fixed size, so only a package pass reaches it.",
                    reference.Name
                );
            }
        }
    }
}
