using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// The one resource tracker. Every storage keeps one instance per frame slot: a current-frame reference reaches this
// frame's instance, and a previous-frame reference the instance the previous frame wrote. An instance is used in two
// roles over its life: as this frame's (the current role) and, one frame later, as the previous frame's (the previous
// role), after which it is idle until its slot comes round again. So in the steady state the first current-role use of a
// frame follows the instance's last previous-role use, or its last current-role use when nothing reads it as the
// previous frame, and the first previous-role use follows the last current-role use. The planner walks those sequences
// once, at compile time, and gives every access its exact prior state and barrier; the render node records exactly
// those and keeps no layout state of its own.
public sealed partial class ShaderPipelineCompiler {
    // The state a pass's reference needs from the instance it reaches, which is also the state the reference leaves it in.
    // A graphics pass's render pass keeps each attachment in its attachment layout, so a later sampling reader's planned
    // barrier is the transition to shader-readable. A preserving write also reads what its predecessor left: a compute
    // pass through the shader, a render pass through the attachment load. A depth test always reads.
    private static ShaderPipelineAccessState UseOf(ShaderPipelinePass pass, ShaderPipelineResource resource, bool write, bool preserve) {
        var buffer = (resource.Kind == ShaderPipelineResourceKind.Buffer);

        if (!write) {
            return new ShaderPipelineAccessState(
                Access: GpuComputeAccess.ShaderRead,
                Layout: (buffer
                    ? GpuImageLayout.Undefined
                    : GpuImageLayout.ShaderReadOnly),
                Stage: (pass.IsGraphics
                    ? GpuComputeStage.FragmentShader
                    : GpuComputeStage.ComputeShader)
            );
        }
        if (resource.Kind == ShaderPipelineResourceKind.Depth) {
            return new ShaderPipelineAccessState(
                Access: GpuComputeAccess.DepthAttachmentRead | GpuComputeAccess.DepthAttachmentWrite,
                Layout: GpuImageLayout.DepthAttachment,
                Stage: GpuComputeStage.FragmentTests
            );
        }
        if (pass.IsGraphics) {
            return new ShaderPipelineAccessState(
                Access: (preserve
                    ? GpuComputeAccess.ColorAttachmentRead | GpuComputeAccess.ColorAttachmentWrite
                    : GpuComputeAccess.ColorAttachmentWrite),
                Layout: GpuImageLayout.RenderTarget,
                Stage: GpuComputeStage.ColorAttachmentOutput
            );
        }

        return new ShaderPipelineAccessState(
            Access: (preserve
                ? GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite
                : GpuComputeAccess.ShaderWrite),
            Layout: (buffer
                ? GpuImageLayout.Undefined
                : GpuImageLayout.General),
            Stage: GpuComputeStage.ComputeShader
        );
    }
    private static ShaderPipelineAccessState Fold(ShaderPipelineAccessState start, List<(int Pass, int Slot, ShaderPipelineAccessState Use)> uses) {
        var state = start;

        foreach (var use in uses) {
            state = state.Then(use: use.Use);
        }

        return state;
    }
    // A graphics pass's attachments, its color attachment first: each loads the contents of a version that forwards its
    // predecessor and clears one whose writer starts from discarded contents, and stores what anything uses afterwards.
    private static IReadOnlyList<ShaderPipelineAttachment> AttachmentsOf(ShaderPipelinePass pass, IReadOnlyDictionary<string, ShaderPipelinePlannedResource> resources) {
        if (!pass.IsGraphics) {
            return [];
        }

        return pass.OutputReferences.Select(selector: output => resources[output.Name]).OrderBy(keySelector: static resource => (resource.Declaration.Kind == ShaderPipelineResourceKind.Depth)).Select(selector: static resource => new ShaderPipelineAttachment(
            Depth: (resource.Declaration.Kind == ShaderPipelineResourceKind.Depth),
            Load: ((resource.Contents == ShaderPipelineContents.Preserved)
                ? GpuAttachmentLoad.Load
                : GpuAttachmentLoad.Clear),
            Storage: resource.Storage,
            Store: (resource.Stored
                ? GpuAttachmentStore.Store
                : GpuAttachmentStore.Discard),
            Version: resource.Name
        )).ToArray();
    }
    private static (IReadOnlyList<ShaderPipelinePlannedResource> Resources, IReadOnlyList<ShaderPipelinePlannedStorage> Storages, ShaderPipelineAccess[][] Accesses) PlanVersions(ShaderPipelineDefinition definition, IReadOnlySet<string> liveResources, IReadOnlyList<ShaderPipelinePlannedPass> passes) {
        var declarations = definition.Resources.Where(predicate: resource => liveResources.Contains(item: resource.Name)).ToDictionary(
            keySelector: static resource => resource.Name,
            comparer: StringComparer.Ordinal
        );
        var successors = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var resource in declarations.Values) {
            if (resource.From is { } predecessor) {
                successors.Add(
                    key: predecessor,
                    value: resource.Name
                );
            }
        }

        // Storages, one per live chain, in the ordinal order of their first versions.
        var roots = declarations.Values.Where(predicate: static resource => (resource.From is null)).OrderBy(
            keySelector: static resource => resource.Name,
            comparer: StringComparer.Ordinal
        ).ToArray();
        var storageOf = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var chains = new List<string>[roots.Length];

        for (var index = 0; (index < roots.Length); index++) {
            var chain = new List<string> { roots[index].Name };

            while (successors.TryGetValue(
                key: chain[^1],
                value: out var next
            )) {
                chain.Add(item: next);
            }
            foreach (var version in chain) {
                storageOf.Add(
                    key: version,
                    value: index
                );
            }
            chains[index] = chain;
        }

        var writers = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var firstUse = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var lastUse = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var readers = new HashSet<string>(comparer: StringComparer.Ordinal);
        // Per storage and role (0 current, 1 previous): the pass, the position in its access list, and the use.
        var roles = new List<(int Pass, int Slot, ShaderPipelineAccessState Use)>[roots.Length, 2];
        var accesses = new (int Storage, string Version, bool PreviousFrame, ShaderPipelineAccessState Use)[passes.Count][];

        for (var storage = 0; (storage < roots.Length); storage++) {
            roles[storage, 0] = [];
            roles[storage, 1] = [];
        }
        foreach (var pass in passes) {
            var declaration = pass.Declaration;
            var list = new List<(int Storage, string Version, bool PreviousFrame, ShaderPipelineAccessState Use)>();

            foreach (var (reference, write) in declaration.InputReferences.Select(selector: static input => (input, false)).Concat(second: declaration.OutputReferences.Select(selector: static output => (output, true)))) {
                var resource = declarations[reference.Name];
                var storage = storageOf[reference.Name];

                var use = UseOf(
                    pass: declaration,
                    preserve: (resource.From is not null),
                    resource: resource,
                    write: write
                );

                roles[storage, (reference.PreviousFrame ? 1 : 0)].Add(item: (pass.Index, list.Count, use));
                list.Add(item: (storage, reference.Name, reference.PreviousFrame, use));
                if (write) {
                    writers[reference.Name] = pass.Index;
                } else {
                    readers.Add(item: reference.Name);
                }
                firstUse.TryAdd(
                    key: reference.Name,
                    value: pass.Index
                );
                lastUse[reference.Name] = pass.Index;
            }
            accesses[pass.Index] = [.. list];
        }

        // The steady state: each role's first use starts where the instance's previous role left it. Folding twice
        // reaches the fixed point, since a fold only ever unions reads into the state it starts from.
        var start = new ShaderPipelineAccessState[roots.Length, 2];

        for (var storage = 0; (storage < roots.Length); storage++) {
            var current = roles[storage, 0];
            var previous = roles[storage, 1];
            var afterCurrent = ShaderPipelineAccessState.Fresh;
            var afterPrevious = ShaderPipelineAccessState.Fresh;

            for (var round = 0; (round < 3); round++) {
                var startCurrent = ((previous.Count != 0)
                    ? afterPrevious
                    : afterCurrent);

                afterCurrent = Fold(
                    start: startCurrent,
                    uses: current
                );
                afterPrevious = Fold(
                    start: afterCurrent,
                    uses: previous
                );
                start[storage, 0] = startCurrent;
                start[storage, 1] = afterCurrent;
            }
            if (roots[storage].IsExternal) {
                // The host owns the instance between frames. The node substitutes the layout the host binds.
                start[storage, 0] = ((roots[storage].Kind == ShaderPipelineResourceKind.Buffer)
                    ? ShaderPipelineAccessState.Host(layout: GpuImageLayout.Undefined)
                    : ShaderPipelineAccessState.Handover(layout: GpuImageLayout.ShaderReadOnly));
            }
        }

        var planned = new ShaderPipelineAccess[passes.Count][];

        for (var index = 0; (index < passes.Count); index++) {
            planned[index] = new ShaderPipelineAccess[accesses[index].Length];
        }
        for (var storage = 0; (storage < roots.Length); storage++) {
            var kind = roots[storage].Kind;

            for (var role = 0; (role < 2); role++) {
                var state = start[storage, role];
                var priorPass = -1;
                var priorKind = (((role == 0) && roots[storage].IsExternal)
                    ? ShaderPipelinePriorKind.Host
                    : ShaderPipelinePriorKind.CrossFrame);

                foreach (var (pass, slot, use) in roles[storage, role]) {
                    var entry = accesses[pass][slot];

                    planned[pass][slot] = new ShaderPipelineAccess(
                        Barrier: ShaderPipelineBarrier.Between(
                            kind: kind,
                            prior: state,
                            use: use
                        ),
                        PreviousFrame: entry.PreviousFrame,
                        Prior: state,
                        PriorKind: priorKind,
                        PriorPass: priorPass,
                        Storage: storage,
                        Use: use,
                        Version: entry.Version
                    );
                    state = state.Then(use: use);
                    priorKind = ShaderPipelinePriorKind.Pass;
                    priorPass = pass;
                }
            }
        }

        var outputs = definition.Outputs.ToHashSet(comparer: StringComparer.Ordinal);
        var storages = new ShaderPipelinePlannedStorage[roots.Length];

        for (var storage = 0; (storage < roots.Length); storage++) {
            var root = roots[storage];
            var written = chains[storage].Any(predicate: writers.ContainsKey);
            var readAsPrevious = (roles[storage, 1].Count != 0);
            var touched = (roles[storage, 0].Count != 0);

            storages[storage] = new ShaderPipelinePlannedStorage(
                Clear: ((root.Initialization != ShaderPipelineInitialization.Zero)
                    ? ShaderPipelineClear.None
                    : (!written
                        ? ShaderPipelineClear.EveryInstance
                        : (readAsPrevious
                            ? ShaderPipelineClear.PreviousInstance
                            : ShaderPipelineClear.None))),
                Declaration: root,
                FrameEnd: (touched
                    ? Fold(
                        start: start[storage, 0],
                        uses: roles[storage, 0]
                    )
                    : (root.IsExternal
                        ? ShaderPipelineAccessState.Handover(layout: GpuImageLayout.ShaderReadOnly)
                        : ShaderPipelineAccessState.Cleared)),
                FrameEndKind: (touched
                    ? ShaderPipelinePriorKind.Pass
                    : (root.IsExternal
                        ? ShaderPipelinePriorKind.Host
                        : ShaderPipelinePriorKind.CrossFrame)),
                History: declarations[chains[storage][^1]].History,
                Index: storage,
                Versions: chains[storage]
            );
        }

        var resources = declarations.Values.OrderBy(
            keySelector: static resource => resource.Name,
            comparer: StringComparer.Ordinal
        ).Select(selector: resource => {
            var successor = successors.GetValueOrDefault(key: resource.Name);
            var retained = (resource.History || outputs.Contains(item: resource.Name));

            return new ShaderPipelinePlannedResource(
                ConsumedAtPassIndex: (((successor is not null) && writers.TryGetValue(
                    key: successor,
                    value: out var overwriter
                ))
                    ? overwriter
                    : -1),
                Contents: (writers.ContainsKey(key: resource.Name)
                    ? ((resource.From is null)
                        ? ShaderPipelineContents.Discarded
                        : ShaderPipelineContents.Preserved)
                    : (resource.IsExternal
                        ? ShaderPipelineContents.External
                        : ShaderPipelineContents.Initialized)),
                Declaration: resource,
                FirstUsePassIndex: firstUse.GetValueOrDefault(
                    key: resource.Name,
                    defaultValue: -1
                ),
                LastUsePassIndex: (retained
                    ? passes.Count
                    : lastUse.GetValueOrDefault(
                        key: resource.Name,
                        defaultValue: -1
                    )),
                Public: outputs.Contains(item: resource.Name),
                Storage: storageOf[resource.Name],
                Stored: (retained || (successor is not null) || readers.Contains(item: resource.Name)),
                Successor: successor,
                WriterPassIndex: writers.GetValueOrDefault(
                    key: resource.Name,
                    defaultValue: -1
                )
            );
        }).ToArray();

        return (resources, storages, planned);
    }
}
