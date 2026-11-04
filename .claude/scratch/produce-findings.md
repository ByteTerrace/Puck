# World CPU Produce-Path Findings (`WorldRenderRoot.ProduceFrame`)

Findings are ranked from highest estimated per-frame CPU cost to lowest across the call tree rooted at `WorldRenderRoot.ProduceFrame` (`src/Puck.World/WorldRenderRoot.cs`). Every symbol and code block below is quoted verbatim from the files read.

---

## 1. `WorldAdjacencySceneEmitter.PrepareFrame`, `WriteRevision`, `PrepareStaticProjections`, and `PublishStaticCopies`: unguarded per-frame neighbour projection, duplicate band selection, and 4× static-copy list rebuilds

### Files and lines
- `src/Puck.World.Client/WorldAdjacencySceneEmitter.cs:147-167`
- `src/Puck.World.Client/WorldAdjacencySceneEmitter.cs:643-646`
- `src/Puck.World.Client/WorldAdjacencySceneEmitter.cs:654-733`
- `src/Puck.World.Client/WorldAdjacencySceneEmitter.Static.cs:24-61`

### Verbatim quotes

`src/Puck.World.Client/WorldAdjacencySceneEmitter.cs:147-167`:
```csharp
    public void PrepareFrame() {
        if (m_liveSlots.Length != m_emittedEntities.Length) { m_liveSlots = new int[m_emittedEntities.Length]; }
        Array.Fill(m_liveSlots, -1);
        m_liveSlotCount = 0;
        var visuals = m_source.Visuals();

        for (var band = 0; (band < Math.Min(visuals.Count, m_bandCount)); band++) {
            SelectBand(visuals[band], m_polledEntities);
            for (var body = 0; (body < m_polledEntities.Length); body++) {
                if (!m_polledEntities[body]) { continue; }
                m_liveSlots[((band * WorldBodiesLimits.CapacityCeiling) + body)] = m_liveSlotCount;
                m_liveSlotCount += WorldRigCatalog.TransformSlotsPerBody;
            }
        }
        PrepareStaticProjections(visuals);
    }

    public int DynamicSlotCount => m_liveSlotCount;
    public IReadOnlyList<SdfMeshInstance> MeshInstances {
        get { PublishStaticCopies(); return m_staticInstances; }
    }
```

`src/Puck.World.Client/WorldAdjacencySceneEmitter.Static.cs:24-61`:
```csharp
    private void PrepareStaticProjections(IReadOnlyList<WorldAdjacencyProjection> projections) {
        m_preparedStatic.Clear();
        m_staticNames.Clear();
        for (var band = 0; band < Math.Min(projections.Count, m_bandCount); band++) {
            var projection = projections[band];
            var image = projection.Neighbour.Definition;
            var selected = WorldAdjacencyGeometry.Select(image, projection.Path[0].Neighbour, projection.OverlapDepth).Placements;
            if (!m_staticProjections.TryGetValue(projection.Name, out var prepared)) {
                prepared = new StaticProjection();
                m_staticProjections.Add(projection.Name, prepared);
            }
            m_staticNames.Add(projection.Name);
            var seed = (image.Generation?.WorldSeed ?? 0UL);
            if (!ReferenceEquals(prepared.Source, image) || !ReferenceEquals(prepared.Creations, image.Creations) || !prepared.Original.SequenceEqual(selected) ||
                !prepared.Path.SequenceEqual(projection.Path) || (prepared.Seed != seed)) {
                prepared.Source = image;
                prepared.Creations = image.Creations;
                prepared.Original = selected.ToArray();
                prepared.Path = projection.Path.ToArray();
                prepared.Seed = seed;
                prepared.Rows = selected.Select(placement => MapIntoSource(placement, projection.Path, image)).ToArray();
                prepared.Image = (image with { PlacementRowsRaw = prepared.Rows });
                prepared.Assets.ReconcileStatic(prepared.Image, prepared.Rows, projection.Name);
                prepared.Assets.Reconcile(image.Creations, [], null, prepared.Rows);
            }
            m_preparedStatic.Add(prepared);
        }
        m_staticRetired.Clear();
        foreach (var name in m_staticProjections.Keys) { if (!m_staticNames.Contains(name)) { m_staticRetired.Add(name); } }
        foreach (var name in m_staticRetired) { m_staticProjections.Remove(name); }
        PublishStaticCopies();
    }
    private void PublishStaticCopies() {
        m_staticInstances.Clear();
        foreach (var prepared in m_preparedStatic) {
            m_staticInstances.AddRange(prepared.Assets.Copies);
        }
    }
```

`src/Puck.World.Client/WorldAdjacencySceneEmitter.cs:643-646`:
```csharp
            bandIndex++;
        }
        PublishStaticCopies();
    }
```

`src/Puck.World.Client/WorldAdjacencySceneEmitter.cs:654-733`:
```csharp
    public void WriteRevision(Span<int> destination) {
        // Poll every band's neighbour reachability/definition-revision — a live neighbour edit (or a neighbour
        // becoming reachable/unreachable) bumps this component so the host rebuilds, exactly like every other
        // watched counter in this composition. One Visuals() call, reused by both passes below.
        var visuals = m_source.Visuals();
...
        for (var bandIndex = 0; (bandIndex < bandLimit); bandIndex++) {
            var projection = visuals[bandIndex];
            var appearanceBase = (bandIndex * WorldBodiesLimits.CapacityCeiling);
            var polled = m_polledEntities.AsSpan();

            _ = SelectBand(
                destination: polled,
                projection: projection
            );

            if (!polled.SequenceEqual(other: m_emittedEntities.AsSpan(
                length: WorldBodiesLimits.CapacityCeiling,
                start: appearanceBase
            ))) {
                m_selectionRevision++;
            }
        }

        destination[0] = m_neighbourRevision;
        destination[1] = m_selectionRevision;
    }
```

### What the code does each frame
1. `SdfCompositionFrameSource.CaptureFrame` calls `emitter.PrepareFrame()` on every emitter (`src/Puck.SdfVm/SdfCompositionFrameSource.cs:545`).
2. `WorldAdjacencySceneEmitter.PrepareFrame` has no revision guard: every frame it fills `m_liveSlots`, calls `m_source.Visuals()`, runs `SelectBand(visuals[band], m_polledEntities)` across every band (`WorldBodiesLimits.CapacityCeiling` bodies per band), and calls `PrepareStaticProjections(visuals)`.
3. `PrepareStaticProjections` runs `WorldAdjacencyGeometry.Select(image, projection.Path[0].Neighbour, projection.OverlapDepth).Placements` for every band, allocates a new placement list inside `WorldAdjacencyGeometry.Select`, compares `!prepared.Original.SequenceEqual(selected)` and `!prepared.Path.SequenceEqual(projection.Path)` across all placements in the band, and calls `PublishStaticCopies()`.
4. Moments later in the same `CaptureFrame` call, `CaptureRevisions` -> `ComputeContentRevision` calls `WorldAdjacencySceneEmitter.WriteRevision`, which calls `m_source.Visuals()` a second time and runs `SelectBand` for every band a second time.
5. `PublishStaticCopies()` (`m_staticInstances.Clear()` followed by `m_staticInstances.AddRange(prepared.Assets.Copies)` for each band) is invoked **4 times per frame**: once at the end of `PrepareStaticProjections` (`Static.cs:54`), twice via the `MeshInstances` property getter (`WorldAdjacencySceneEmitter.cs:166`, read by `m_emitters.Sum(selector: emitter => emitter.MeshInstances.Count)` at `SdfCompositionFrameSource.cs:556` and `foreach (var copy in emitter.MeshInstances)` at `SdfCompositionFrameSource.cs:502`), and once at the end of `PackDynamicTransforms` (`WorldAdjacencySceneEmitter.cs:645`).

### Why it costs
- Whole-neighbour placement geometry selection (`WorldAdjacencyGeometry.Select`) and sequence comparisons run every frame even when the neighbour's `DefinitionRevision` and `Path` have not changed.
- `m_source.Visuals()` and `SelectBand` run twice per frame on identical inputs (`PrepareFrame` and `WriteRevision`).
- `PublishStaticCopies()` clears and copies all static `SdfMeshInstance` references across all adjacency bands 4 times per frame.

### Fix
- Cache the `visuals` list and per-band `SelectBand` results in `PrepareFrame()` (and compute `m_neighbourRevision` / `m_selectionRevision` right there) so `WriteRevision` simply writes the two ints discovered in `PrepareFrame()`.
- In `PrepareStaticProjections`, check `ReferenceEquals(prepared.Source, image) && ReferenceEquals(prepared.Creations, image.Creations) && (prepared.Seed == seed) && prepared.Path.SequenceEqual(projection.Path)` **before** calling `WorldAdjacencyGeometry.Select(...)`; since `WorldDefinition` is immutable per revision, identical `image`, `seed`, and `Path` guarantee identical `selected` placements without running `WorldAdjacencyGeometry.Select` or `SequenceEqual`.
- Rebuild `m_staticInstances` only when `PrepareStaticProjections` actually changes a band's static assets, and make `MeshInstances => m_staticInstances` return the cached list without calling `PublishStaticCopies()`.

---

## 2. `SdfWorldTables.Cadence` and `SdfWorldPassRecorder`: repeated 3.5 KB `SdfFrameBlock.Write` serialization, FNV-1a hashing, and 12 KB+ `XxHash3` region hashing per view and per pass every frame

### Files and lines
- `src/Puck.SdfVm/SdfWorldTables.Cadence.cs:78-140`
- `src/Puck.SdfVm/SdfWorldTables.Cadence.cs:217-252`
- `src/Puck.SdfVm/SdfWorldTables.Cadence.cs:270-354`
- `src/Puck.SdfVm/SdfWorldPasses.Cadence.cs:8-48`
- `src/Puck.SdfVm/SdfWorldPassRecorder.cs:430-431`
- `src/Puck.SdfVm/SdfWorldPassRecorder.cs:495-502`

### Verbatim quotes

`src/Puck.SdfVm/SdfWorldTables.Cadence.cs:78-110`:
```csharp
    public ulong PassSignature(SdfFrame frame, int view, string part) {
        var block = m_signatureBlock.AsSpan();

        block.Clear();
        SdfFrameBlock.Write(block: block, frame: frame, height: 0, tables: PassValues, view: view, width: 0);
        var composite = Fnv1aHash.Create();

        AddMembers(block: block, hash: ref composite, members: CompositeValues);
        ClearMembers(block: block, members: CompositeValues);
        var lighting = Fnv1aHash.Create();

        lighting.Add(value: m_lightingSignature);
        // Submit records the environment before signatures are read; a sub-code candidate keeps this revision.
        lighting.Add(value: unchecked((ulong)m_skyEnvironment.Renders));
        lighting.Add(value: unchecked((ulong)m_screenEmission.Renders));
        lighting.Add(value: m_shadowSignature);
        lighting.Add(values: block);
        var shadow = Fnv1aHash.Create();

        shadow.Add(value: m_shadowSignature);
        AddShadowOwners(hash: ref shadow, slots: frame.Lights.ShadowSlots);
        AddMembers(block: block, hash: ref shadow, members: ShadowValues);
        ClearMembers(block: block, members: ShadowValues);
        ClearMembers(block: block, members: LightingValues);
        var geometry = Fnv1aHash.Create();

        geometry.Add(value: m_geometrySignature);
        AddBrickGeometry(hash: ref geometry);
        geometry.Add(values: block);
        var hash = Fnv1aHash.Create();

        hash.Add(value: geometry.Value);
```

`src/Puck.SdfVm/SdfWorldTables.Cadence.cs:217-240`:
```csharp
    public ulong ViewSignature(SdfFrame frame, int view) {
        var hash = Fnv1aHash.Create();
        var block = m_signatureBlock.AsSpan();
        Span<byte> tables = stackalloc byte[sizeof(ulong)];

        block.Clear();
        SdfFrameBlock.Write(
            block: block,
            frame: frame,
            height: 0u,
            tables: PassValues,
            view: view,
            width: 0u
        );
        MemoryMarshal.Write(
            destination: tables,
            value: in m_tablesSignature
        );
        hash.Add(values: tables);
        AddBrickGeometry(hash: ref hash);
        hash.Add(value: unchecked((ulong)m_skyEnvironment.Renders));
        hash.Add(value: unchecked((ulong)m_screenEmission.Renders));
        hash.Add(values: block);
```

`src/Puck.SdfVm/SdfWorldTables.Cadence.cs:270-299`:
```csharp
    public void UpdateTablesSignature() {
        // Each region's bytes are hashed once, word-wide, and the signatures fold those hashes.
        var volumes = XxHash3.HashToUInt64(source: m_volumeRegion.Contents);
        var lights = XxHash3.HashToUInt64(source: m_lightRegion.Contents);
        var skies = XxHash3.HashToUInt64(source: m_skyRegion.Contents);
        var skyLayers = XxHash3.HashToUInt64(source: m_skyLayerRegion.Contents);
        var handoffs = XxHash3.HashToUInt64(source: MemoryMarshal.AsBytes(span: m_shadowHandoffs.AsSpan(length: m_shadowHandoffCount, start: 0)));
        var screenSurfaces = XxHash3.HashToUInt64(source: m_screenSurfaceRegion.Contents);
        var screenMappings = XxHash3.HashToUInt64(source: m_screenMappingRegion.Contents);
        var hash = Fnv1aHash.Create();
```

`src/Puck.SdfVm/SdfWorldPasses.Cadence.cs:23-48`:
```csharp
        UpdateSurfaceInputs(entry: entry, tables: tables, view: view);
        if (entry.CadenceFrame != m_frame) {
            entry.CadenceFrame = m_frame;
            if (!entry.Temporal.Stands(epoch: EpochOf(entry: entry, view: view, width: entry.Temporal.Epoch.Width,
                height: entry.Temporal.Epoch.Height, debug: tables.PassValues.DebugMode, temporal: temporal,
                unread: entry.UnreadFrames), previousPoses: tables.PoseRevision)) {
                entry.SampleRevision++;
            }
        }
        var hash = Fnv1aHash.Create();

        hash.Add(value: tables.PassSignature(frame: frame, view: Math.Min(val1: view.View, val2: (frame.Views.Count - 1)), part: part));
        hash.Add(value: entry.Bindings);
        hash.Add(value: entry.SampleRevision);
        return ((part == SdfWorldPackage.Parts.ShadowFar) ? FarSignatureOf(entry: entry, signature: hash.Value) : hash.Value);
    }

    private static void UpdateSurfaceInputs(Entry entry, SdfWorldView view, SdfWorldTables tables) {
        var frame = view.Residency.Frame!;
        var signature = tables.PassSignature(frame: frame, view: Math.Min(val1: view.View, val2: (frame.Views.Count - 1)), part: SdfWorldPackage.Parts.Views);

        if ((entry.SurfaceSignature != signature) || (entry.RenderedScale != entry.CurrentScale)) {
            entry.SurfaceSignature = signature;
            entry.Temporal.Changed();
        }
    }
```

### What the code does each frame
1. For every prepared residency, `SdfWorldTables.UpdateTablesSignature()` hashes the full capacity of `m_volumeRegion.Contents` (12,288 bytes), `m_lightRegion.Contents`, `m_skyRegion.Contents`, `m_skyLayerRegion.Contents`, `m_screenSurfaceRegion.Contents`, and `m_screenMappingRegion.Contents` with `XxHash3.HashToUInt64`, even though `GpuRegion.Write` already returned `bool` indicating whether any bytes in those regions changed.
2. During `RenderGraphRuntime.WithUnchanged` (`src/Puck.Shaders/Graph/RenderGraphRuntime.Packages.cs:125-166`), `SdfWorldPasses.IsUnchanged` calls `UpdateSurfaceInputs` (which calls `tables.PassSignature(frame, view, Views)`) and then `tables.ViewSignature(frame, view)`.
3. During `ShaderPipelineRenderNode.RecordPasses`, for each of the ~10 passes in an `sdf.world` view graph (`shadow-near`, `shadow-far`, `shadow-second`, `mesh`, `pyramid`, `surface`, `views`, `sky`, `glass-clear`, `glass`, `glass-shade`, `composite`), `RecordPackage` calls `pass.Package.Signature(...)` -> `SdfWorldPasses.SignatureOf`.
4. Each call to `SignatureOf` calls `UpdateSurfaceInputs` (which calls `tables.PassSignature(frame, view, Views)` **again** for every single pass!) and then calls `tables.PassSignature(frame, view, part)`.
5. Every `PassSignature` and `ViewSignature` call zeroes `m_signatureBlock` (3,584 bytes), runs `SdfFrameBlock.Write` to serialize all view matrices, lights, sky, and parameters into `m_signatureBlock`, and runs byte-by-byte `Fnv1aHash.Add(values: block)` over the 3,584-byte block **twice** in `PassSignature` (`lighting.Add(values: block)` at line 95 and `geometry.Add(values: block)` at line 106) and once in `ViewSignature` (line 239).
6. Then `SdfWorldPassRecorder.Record` (`SdfWorldPassRecorder.cs:495`) calls `SdfFrameBlock.Write` a third time per pass to fill `recording.PassBlock`.
7. Across a single view with ~10 passes, `SdfFrameBlock.Write` (3.5 KB serialization) runs ~30 times per frame and byte-by-byte `Fnv1aHash` runs over ~65 KB of `m_signatureBlock` per view per frame; multiplied across the main view, camera views, and nested session views, this is hundreds of `SdfFrameBlock.Write` + FNV-1a passes every frame.

### Why it costs
- `Fnv1aHash.Add(ReadOnlySpan<byte>)` is a serial byte-at-a-time loop (`1 byte` per multiply-xor), and `PassSignature` hashes 3,584 bytes twice per call while `SignatureOf` invokes `PassSignature` twice per pass (once in `UpdateSurfaceInputs` and once for `part`).
- The four partial hashes (`composite`, `lighting`, `shadow`, `geometry`) in `PassSignature` depend only on `(frame, view)`, not on `part`, yet they are recomputed from scratch for every `part` of the same `(frame, view)` and again inside `UpdateSurfaceInputs`.
- `UpdateTablesSignature` re-hashes unchanged `GpuRegion` buffers every frame instead of caching each region's hash when `GpuRegion.Write` returns `false`.

### Fix
- Compute and cache the per-view `(composite, lighting, shadow, geometry, viewSignature)` hashes **once per `(frame, view)`** when `SdfWorldTables.UpdateTablesSignature()` runs (and use `XxHash3.HashToUInt64` instead of byte-wise `Fnv1aHash` over the 3,584-byte `m_signatureBlock`). `PassSignature(frame, view, part)` and `ViewSignature(frame, view)` then become $O(1)$ table lookups with zero `SdfFrameBlock.Write` or byte hashing per pass.
- Guard `UpdateSurfaceInputs` by `if (entry.CadenceFrame != m_frame)` so it runs once per view per frame instead of once per pass.
- In `SdfWorldTables`, update each region's `XxHash3` only when `GpuRegion.Write(...)` returns `true` (or hash only the active prefix `volumes.Count * SdfVolume.ByteCount`).

---

## 3. `SdfCompositionFrameSource.CaptureFrame`, `PackTransforms`, `SdfMeshPathBake.PrepareFrame`, and `SdfMeshDresser.Dress`: per-frame full-world static + dynamic mesh copy repack, layout comparison, and group dictionary scans

### Files and lines
- `src/Puck.SdfVm/SdfCompositionFrameSource.cs:496-511`
- `src/Puck.SdfVm/SdfCompositionFrameSource.cs:545-561`
- `src/Puck.SdfVm/SdfMeshPathBake.cs:369-414`
- `src/Puck.SdfVm/SdfMeshPathBake.Residency.cs:168-179`
- `src/Puck.SdfVm/SdfMeshPathBake.Settle.cs:15-61`
- `src/Puck.SdfVm/SdfMeshDresser.cs:61-103`
- `src/Puck.SdfVm/SdfMeshDresser.cs:202-214`
- `src/Puck.SdfVm/SdfWorldResidency.cs:853-862`

### Verbatim quotes

`src/Puck.SdfVm/SdfCompositionFrameSource.cs:496-511`:
```csharp
        m_meshInstances.Clear();
        m_meshAssetSlots.Clear();
        var root = m_programSlots;

        foreach (var emitter in m_emitters) {
            m_meshAssetSlots.AddRange(collection: emitter.MeshAssetSlots);
            foreach (var copy in emitter.MeshInstances) {
                copy.FrameSlot = root;
                var previous = m_transforms[root];

                m_transforms[root] = copy.Transform;
                if (previous != copy.Transform) { m_moved.Owe(count: 1, start: root); }
                m_meshInstances.Add(item: copy);
                root++;
            }
        }
```

`src/Puck.SdfVm/SdfCompositionFrameSource.cs:545-561`:
```csharp
        foreach (var emitter in m_emitters) { emitter.PrepareFrame(); }
        var slots = 0;
        var layoutChanged = false;

        for (var index = 0; (index < m_emitters.Count); index++) {
            layoutChanged |= (m_slotBases[index] != slots);
            m_slotBases[index] = slots;
            slots = checked((slots + m_emitters[index].DynamicSlotCount));
        }
        layoutChanged |= (slots != m_programSlots);
        m_programSlots = slots;
        var roots = m_emitters.Sum(selector: emitter => emitter.MeshInstances.Count);
```

`src/Puck.SdfVm/SdfMeshPathBake.cs:401-414`:
```csharp
    private bool SameCopyLayout(SdfFrame frame) {
        if ((m_copyLayout.Length != frame.MeshInstances.Count) || (m_copyAssetSlots.Length != frame.MeshAssetSlots.Count)) { return false; }
        for (var index = 0; index < m_copyAssetSlots.Length; index++) { if (m_copyAssetSlots[index] != frame.MeshAssetSlots[index]) { return false; } }
        for (var index = 0; index < m_copyLayout.Length; index++) {
            var old = m_copyLayout[index];
            var copy = frame.MeshInstances[index];
            if ((old.AssetSlot != copy.AssetSlot) || (old.TemplateRange != copy.TemplateRange) || (old.IsDynamic != copy.IsDynamic) || (old.HasMesh != (copy.Mesh is not null)) ||
                (old.OtherAssetSlots.Length != copy.OtherAssetSlots.Count)) { return false; }
            for (var surface = 0; surface < old.OtherAssetSlots.Length; surface++) {
                if (old.OtherAssetSlots[surface] != copy.OtherAssetSlots[surface]) { return false; }
            }
        }
        return true;
    }
```

`src/Puck.SdfVm/SdfMeshDresser.cs:68-75, 97-102, 202-205`:
```csharp
            var groups = access.Groups;
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++) {
                var group = groups[groupIndex];
                if (access.MeshOf(group) is not { } mesh) { continue; }
                finished++;
                topologyChanged |= (!m_dressedGroups.TryGetValue(group, out var heldMesh) || !ReferenceEquals(mesh, heldMesh));
            }
            topologyChanged |= (finished != m_drawnMeshes);
```
```csharp
                        for (var index = 0; index < m_drawnCopies.Length; index++) {
                            if ((m_drawnCopies[index] != frame.MeshInstances[index]) || (m_drawnCopyFrames[index] != frame.MeshInstances[index].FrameSlot)) {
                                AddBindings(m_copyHeads, index);
                            }
                        }
```
```csharp
    private void CommitInputs(SdfFrame frame) {
        if (m_drawnCopies.Length != frame.MeshInstances.Count) { m_drawnCopies = new SdfMeshInstance[frame.MeshInstances.Count]; m_drawnCopyFrames = new int[frame.MeshInstances.Count]; }
        for (var index = 0; index < m_drawnCopies.Length; index++) { m_drawnCopies[index] = frame.MeshInstances[index]; m_drawnCopyFrames[index] = frame.MeshInstances[index].FrameSlot; }
```

`src/Puck.SdfVm/SdfWorldResidency.cs:853-862`:
```csharp
        var glass = 0;

        for (var index = 0; (index < frame.MeshDraws.Count); index++) {
            if (frame.MeshDraws[index].Glass) { glass++; }
        }
        Volatile.Write(
            location: ref m_meshDrawCount,
            value: frame.MeshDraws.Count
        );
        Volatile.Write(location: ref m_glassDrawCount, value: glass);
```

### What the code does each frame
Every frame, for every residency that captures a frame:
1. `SdfCompositionFrameSource.CaptureFrame` allocates a delegate + interface enumerator for `m_emitters.Sum(selector: emitter => emitter.MeshInstances.Count)`, then in `PackTransforms` clears `m_meshInstances` and `m_meshAssetSlots` and loops over every static and dynamic `SdfMeshInstance` copy across all emitters, assigning `copy.FrameSlot = root`, comparing `m_transforms[root] != copy.Transform`, writing `m_transforms[root] = copy.Transform`, and adding `copy` to `m_meshInstances` (while boxing `IReadOnlyList` enumerators).
2. `SdfMeshPathBake.PrepareFrame` runs `SameCopyLayout(frame)`, which loops over every `m_copyAssetSlots` and every `m_copyLayout` entry (every static + dynamic rigid copy in the world) and its `OtherAssetSlots`.
3. `SdfMeshPathBake.PrepareFrame` then calls `Prioritize()` (`SdfMeshPathBake.Residency.cs:168-179`), which scans `m_groups` twice, and `Settle(frame)` (`SdfMeshPathBake.Settle.cs:15-61`), which scans `m_groups` two more times (even though static groups have `!group.Dynamic` and `group.Replaces is null`).
4. `SdfMeshDresser.Dress` then loops over `access.Groups` (`m_groups`) a 5th time (`SdfMeshDresser.cs:69-74`), performing a `Dictionary<SdfMeshBakeGroup, SdfMesh>.TryGetValue` lookup on every group even when `m_productRevision == access.Revision` (no mesh landed or changed).
5. `SdfMeshDresser.Dress` then loops over all `m_drawnCopies.Length` (all static + dynamic rigid copies) to compare `m_drawnCopies[index] != frame.MeshInstances[index]`, and in `CommitInputs` loops over all `m_drawnCopies.Length` again to copy `frame.MeshInstances[index]` and `FrameSlot` into `m_drawnCopies` and `m_drawnCopyFrames`.
6. Finally, `SdfWorldResidency.Capture` (`SdfWorldResidency.cs:855-857`) loops over all `frame.MeshDraws.Count` to count `Glass` draws, even when `frame.MeshDrawsRevision` did not change and even though `SdfWorldTables.StageMeshRegionCore` already tracks `m_glassDrawCount`.

### Why it costs
- Static rigid copies (from `WorldRigidAssets` and `WorldAdjacencySceneEmitter`) do not move between static revisions, yet every frame iterates, repacks, compares, and copies all static and dynamic `SdfMeshInstance` objects at least 6 times (`PackTransforms`, `SameCopyLayout`, `Dress` comparison, `CommitInputs` copy, plus `PublishStaticCopies`).
- `m_groups` (all bake groups in the program, predominantly static) is scanned 5 times per frame per residency, including a dictionary lookup per group in `SdfMeshDresser.Dress` when `m_productRevision == access.Revision`.

### Fix
- Give `SdfFrame` a `MeshCopyLayoutRevision` (incremented only when emitters reconcile static/dynamic rigid assets or program slots change) so `SameCopyLayout` is a single integer comparison (`m_copyLayoutRevision == frame.MeshCopyLayoutRevision`), and `PackTransforms` only updates the dynamic copies slice of `m_meshInstances` / `m_transforms` while keeping the static copies prefix intact.
- In `SdfMeshDresser.Dress`, check `m_dressedGroups.TryGetValue` only when `m_productRevision != access.Revision`, and track changed copy indices via `SdfMovedTransforms` (or a dirty list of dynamic copies) instead of scanning and copying all `m_drawnCopies` twice per frame.
- In `SdfMeshPathBake`, maintain a small list of active/unfinished groups for `Prioritize()` and a list of dynamic groups (`group.Dynamic`) for `Settle(frame)` instead of scanning all `m_groups` 4 times per frame.
- In `SdfWorldResidency.Capture`, only recount `glass` when `frame.MeshDrawsRevision` changes (or read it from `SdfMeshDresser`).

---

## 4. `WorldCreationInstances`: $O(N^2)$ `RootSlotOffset` loop and unguarded per-registration `RootPose`, `AppendVolumes`, `WorldPortalStraddle.Clip`, and `SetMeshDraw` across the entire pool every frame

### Files and lines
- `src/Puck.World.Client/WorldCreationInstances.cs:302-308`
- `src/Puck.World.Client/WorldCreationInstances.cs:1163-1169`
- `src/Puck.World.Client/WorldCreationInstances.Pack.cs:153-156`
- `src/Puck.World.Client/WorldCreationInstances.Pack.cs:195-200`
- `src/Puck.World.Client/WorldCreationInstances.Pack.cs:226-235`
- `src/Puck.World.Client/WorldCreationInstances.Pack.cs:325-373`

### Verbatim quotes

`src/Puck.World.Client/WorldCreationInstances.cs:302-308`:
```csharp
    public int SlotsAt(int index) => (m_pool[index]?.Slots ?? 0);
    public int RootSlotOffset(int index) {
        var offset = 0;

        for (var entry = 0; (entry < index); entry++) { offset = checked((offset + SlotsAt(index: entry))); }
        return offset;
    }
```

`src/Puck.World.Client/WorldCreationInstances.Pack.cs:153-156`:
```csharp
        for (var index = 0; (index < m_pool.Length); index++) {
            var rootSlot = (slotBase + RootSlotOffset(index: index));
            var slots = SlotsAt(index: index);
            var live = m_pool[index];
```

`src/Puck.World.Client/WorldCreationInstances.Pack.cs:325-373`:
```csharp
            NoteRate(center: rootPosition, live: live, packedSlots: packedSlots, posed: posed, radius: RateReach(live: live, placementScale: placementScale), rootSlot: rootSlot);
            AppendVolumes(
                document: document,
                hidden: live.Hidden,
                live: live,
                placementScale: placementScale,
                rootSlot: rootSlot,
                shapeCount: shapeCount
            );

            // A body straddling a portal whose counterpart stands in this world is drawn on both sides of it. The rest
            // bound screens out the bodies nowhere near a door; the posed parts decide.
            if (
                (live.BodyIndex is { } straddler) &&
                (doors.Count > 0) &&
                (WorldPortalStraddle.Clip(
                    center: transforms[rootSlot].Position,
                    doors: doors,
                    firstSlot: rootSlot,
                    radius: (ReachOf(live: live) * placementScale),
                    slotCount: slots
                ) is not null) &&
                (WorldPortalStraddle.Clip(
                    doors: doors,
                    firstSlot: rootSlot,
                    slotCount: slots,
                    spheres: m_partSpheres.AsSpan(start: 0, length: PartSpheres(live: live, placementScale: placementScale, rootSlot: rootSlot, shapeCount: shapeCount, transforms: transforms))
                ) is { } clip)
            ) {
                m_slotClipRun.Add(item: clip);
                m_slotClipBodies.Add(item: straddler);
            }

            // The root slot holds the pose the shapes were packed under, followers included, whether or not this frame
            // repacked it.
            SetMeshDraw(
                draw: ((live.Mesh is { } mesh)
                    ? WorldPlacementStamper.MeshDrawOf(
                        identity: live,
                        material: live.MeshMaterial,
                        mesh: mesh,
                        origin: transforms[rootSlot].Position,
                        rotation: transforms[rootSlot].Orientation,
                        scale: placementScale
                    ) with { IsDynamic = true }
                    : null),
                index: index
            );
        }
```

### What the code does each frame
1. In `WorldCreationInstances.PackTransforms` (`WorldCreationInstances.Pack.cs:153-155`), the loop over `0 .. m_pool.Length - 1` calls `RootSlotOffset(index: index)` on every iteration. `RootSlotOffset(index)` loops from `0` to `index` summing `SlotsAt(entry)`. This turns a simple running sum into an $O(N^2)$ loop over `m_pool` every frame (and `Emit` in `WorldCreationInstances.Emit.cs` does the same).
2. For every non-null registration in `m_pool`, even when `Wake(...)` returns `false` (meaning the registration is at rest and its transforms were retained without repacking), `PackTransforms` still unconditionally calls `RootPose`, `NoteRate`, `AppendVolumes`, `WorldPortalStraddle.Clip`, and `SetMeshDraw` (which constructs a new `SdfMeshDraw` struct via `WorldPlacementStamper.MeshDrawOf` including `Matrix4x4` TRS multiplication and compares it against `m_meshDraws`).
3. Furthermore, bounded volumes are cleared and re-copied across three separate lists every frame: `WorldRigidAssets.Volumes` (`src/Puck.World.Client/WorldRigidAssets.cs:74-80`), `WorldCreationInstances.Volumes` (`src/Puck.World.Client/WorldCreationInstances.cs:1163-1169`), and `WorldFramePresenter.Dress` (`src/Puck.World.Client/WorldFramePresenter.cs:2187-2189`), and then `SdfWorldTables.PackVolumes` (`src/Puck.SdfVm/SdfWorldTables.FramePreparation.cs:193-238`) clears all 12,288 bytes of `m_volumeScratch` and repacks every volume every frame.

### Why it costs
- `RootSlotOffset(index)` does $\frac{N(N-1)}{2}$ array reads and additions per frame across `m_pool` (which holds up to `WorldCreationInstances` capacity).
- Sleeping/at-rest registrations still pay `RootPose`, `AppendVolumes`, `WorldPortalStraddle.Clip`, and `WorldPlacementStamper.MeshDrawOf` matrix construction every frame.

### Fix
- Maintain a running `var rootOffset = 0;` inside `PackTransforms` (incremented by `slots` at the end of each iteration) and store `live.RootOffset` on `Registration` during `Reconcile` so `RootSlotOffset(index)` is $O(1)$.
- When `Wake(...) || live.RateOwed` is `false` and `packedBefore` is `true`, skip `SetMeshDraw` (the registration's `transforms[rootSlot]` and `placementScale` did not change) and cache each registration's volumes and portal clip state while at rest.

---

## 5. `WorldRigidAssets.Pack` and `PackTemplates`: per-frame evaluation of all rigid bodies (including sleeping bodies), template slot overwrites, and LINQ closure allocation

### Files and lines
- `src/Puck.World.Client/WorldRigidAssets.cs:219-281`
- `src/Puck.World.Client/WorldRigidAssets.Static.cs:96-98`

### Verbatim quotes

`src/Puck.World.Client/WorldRigidAssets.Static.cs:96-98`:
```csharp
    private void PackTemplates(Span<DynamicTransform> transforms, int slotBase) {
        for (var index = 0; (index < m_slotCount); index++) { transforms[slotBase + index] = new DynamicTransform(Vector3.Zero, Quaternion.Identity); }
    }
```

`src/Puck.World.Client/WorldRigidAssets.cs:219-281`:
```csharp
    public void Pack(Span<DynamicTransform> transforms, IWorldStampSource source, int slotBase, float deltaSeconds = 0) {
        var doors = WorldPortalStraddle.Doors(definition: source.Definition);

        m_rootVolumes.Clear();
        PackTemplates(transforms: transforms, slotBase: slotBase);
        for (var index = 0; (index < m_bodies.Count); index++) {
            var body = m_bodies[index];

            if (!m_targets.TryGetValue(key: body.BodyIndex, value: out var target) || (target.Prototype != body.Creation.Id)) {
                target = new WorldPickTarget(null, body.BodyIndex) { CreationSlots = 1, Prototype = body.Creation.Id };
                m_targets[body.BodyIndex] = target;
            }
            var position = source.Position(index: body.BodyIndex);
            var orientation = source.Orientation(index: body.BodyIndex);

            if (!m_motion.TryGetValue(key: body.BodyIndex, value: out var motion)) { m_motion.Add(key: body.BodyIndex, value: motion = new()); }
            motion.Reads.Bind(mirror: source.StateMirror, bodyIndex: body.BodyIndex);
            motion.Reads.Arrive(first: body.Creation, second: body.Look);
            var lanes = WorldLookLaneEvaluator.EvaluateLanes(expressions: body.Look.Motion.Lanes, reads: motion.Reads);
...
            var clip = WorldPortalStraddle.Clip(doors: doors, center: position, radius: (m_reaches[asset] * body.Scale), firstSlot: 0, slotCount: 1);

            if (!previous.SamePose(firstSlot, target, motionIdentity, transform, body.Scale, true, clip, mesh, material, otherSlots)) {
                m_copies[index] = new SdfMeshInstance(firstSlot, target, transform, body.Scale, IsDynamic: true) { Clip = clip, Material = material, Mesh = mesh, MotionIdentity = motionIdentity, OtherAssetSlots = otherSlots };
            }
            foreach (var volume in (body.Creation.EngineDocument.Volumes ?? [])) {
                if (!volume.Enabled) { continue; }
                if ((volume.Parent is { } parentName) && WorldShapeIsolation.IsHidden(hidden: hidden, name: parentName, shapes: (body.Creation.EngineDocument.Shapes ?? []))) { continue; }
                if (m_rootVolumes.Count == SdfProgramBuilder.MaxVolumes) { throw new InvalidOperationException(message: $"Creation '{body.Creation.Id}' on body {body.BodyIndex} exceeds the {SdfProgramBuilder.MaxVolumes}-volume frame capacity."); }
                var parent = body.Creation.EngineDocument.Shapes?.FirstOrDefault(predicate: shape => (shape.Name?.Value == volume.Parent));

                m_rootVolumes.Add(item: (index, volume.ToVolume(dynamicSlot: 0, origin: ((parent?.Position.Value ?? Vector3.Zero) * body.Scale), rotation: (parent?.Rotation.Value ?? Quaternion.Identity), scale: body.Scale)));
            }
        }
    }
```

### What the code does each frame
1. `PackTemplates` writes `new DynamicTransform(Vector3.Zero, Quaternion.Identity)` into every template slot every frame.
2. `WorldRigidAssets.Pack` loops over all `m_bodies` every frame without any `WorldTransformOwners` sleep/wake check: for every rigid body in the world, it looks up `m_targets` and `m_motion` in dictionaries, binds and evaluates `motion.Reads` and `WorldLookLaneEvaluator.EvaluateLanes`, resolves `WorldDynamicsResponse.TryResolveResponse`, runs `WorldPortalStraddle.Clip` against all doors, checks `previous.SamePose` (allocating a new `SdfMeshInstance` class instance whenever any pose field changes instead of mutating or keeping value state), and rebuilds `m_rootVolumes` (allocating a closure for `FirstOrDefault(predicate: shape => (shape.Name?.Value == volume.Parent))` at line 277 whenever a rigid body creation has a volume).

### Why it costs
- Unlike articulated registrations in `WorldCreationInstances`, rigid bodies in `WorldRigidAssets` have no `WorldTransformOwners` sleep/wake gating, so every rigid body in the world runs state-read binding, lane evaluation, dynamics lookup, dictionary lookups, and portal-door straddle checks every frame.
- Every moving rigid body allocates a new `SdfMeshInstance` heap object each frame at line 271 (`new SdfMeshInstance(...)`), and any rigid body with a volume allocates a closure for `FirstOrDefault` at line 277.

### Fix
- Pre-resolve `target`, `motion`, `asset` index, and resolved volume parent offsets on each `m_bodies` entry during `Reconcile` instead of doing dictionary lookups (`m_targets`, `m_motion`, `m_indices`) and `FirstOrDefault` every frame.
- Gate each rigid body with `WorldTransformOwners` (just as `WorldCreationInstances` and `WorldAdjacencySceneEmitter` do) so sleeping rigid bodies with unchanged root pose, lanes, and state skip evaluation.

---

## 6. `SdfWorldPasses.BeginFrame` and `SdfMeshIrradiance.Stage`: preparing unread/held residencies every frame, and per-frame static draw hashing and sky packing in `SdfMeshIrradiance`

### Files and lines
- `src/Puck.SdfVm/SdfWorldPasses.cs:416-433`
- `src/Puck.SdfVm/SdfMeshIrradiance.cs:494-500`
- `src/Puck.SdfVm/SdfMeshIrradiance.cs:994-1015`
- `src/Puck.SdfVm/SdfMeshIrradiance.cs:1060-1085`

### Verbatim quotes

`src/Puck.SdfVm/SdfWorldPasses.cs:416-433`:
```csharp
        foreach (var residency in m_residencies.Keys) {
            if (!residency.IsReleased) {
                Begin(residency: residency);
            }
        }
        // A routed residency captures its host while preparing, so every residency must start before any is prepared.
        foreach (var residency in m_residencies.Keys) {
            if (!residency.IsReleased) {
                _ = residency.Prepare(context: in context);
            }
        }

        // A capture bypasses cadence, and an installed graph need not ask for its counter. Resolve its view before
        // recording anyway, after the host's capture has latched the routes this frame presents.
        foreach (var instance in m_entries.Keys) {
            _ = Refresh(instance: instance);
        }
```

`src/Puck.SdfVm/SdfMeshIrradiance.cs:994-1015`:
```csharp
    private Task? RefreshScene(SdfFrame frame, bool bakePending = false) {
        var key = 17;
        var draws = 0;

        var meshDraws = frame.MeshDraws;

        for (var index = 0; (index < meshDraws.Count); index++) {
            var draw = meshDraws[index];

            if (!SdfMeshIrradianceScene.IsStatic(draw: in draw)) { continue; }
            key = HashCode.Combine(value1: key, value2: System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: draw.Mesh), value3: draw.ObjectToWorld, value4: draw.Material);
            draws++;
        }
        // Even a frame matching the installed scene waits for an older build: that build could otherwise replace
        // the scene while this frame traces it. The lockstep caller resolves this frame again after it lands.
        if (m_build is { IsCompleted: false }) { return m_build; }
        if (key == m_sceneKey) { return null; }
        if (key == m_buildingKey) { return m_build; }
        if (bakePending && (draws == 0)) { return null; }

        return StartSceneBuild(draws: draws, frame: frame, key: key);
    }
```

`src/Puck.SdfVm/SdfMeshIrradiance.cs:1060-1085`:
```csharp
    private void RefreshLighting(SdfFrame frame, Layout layout) {
        var localKey = HashCode.Combine(value1: frame.IndirectGains, value2: frame.IndirectSources, value3: MaterialsKey(program: frame.Program));
        var skyKey = 0;
        var records = frame.Lights.Records;

        for (var index = 0; (index < frame.Lights.Count); index++) {
            var light = records[index];

            if (light.IsTraced && (light.Kind != SdfLightKind.Directional)) { localKey = HashCode.Combine(value1: localKey, value2: index, value3: light); }
        }

        var key = HashCode.Combine(value1: localKey, value2: layout.Bounce);

        for (var index = 0; (index < frame.Lights.Count); index++) {
            var light = records[index];

            if (light.Kind == SdfLightKind.Directional) { key = HashCode.Combine(value1: key, value2: index, value3: light); }
        }
        if ((frame.IndirectSources & SdfIndirectSources.Sky) != 0) {
            frame.Sky.Pack(frame.Lights, frame.FarDistance, m_skyDetails, out var block, m_skyLayers);
            skyKey = block.GetHashCode();
            foreach (var layer in m_skyLayers) { skyKey = HashCode.Combine(value1: skyKey, value2: layer); }
        }
        if ((key == m_lightingKey) && (skyKey == m_skyKey) && (m_lighting is not null)) { return; }
        if (!frame.ConvergesLighting && (m_lighting is not null) && (Stopwatch.GetElapsedTime(startingTimestamp: m_lightingCheckedAt) < LightingInterval)) { return; }
```

### What the code does each frame
1. `SdfWorldPasses.BeginFrame` loops over **every** registered `SdfWorldResidency` in `m_residencies.Keys` and calls `residency.Prepare(context: in context)` on the frame thread before `RenderGraphScheduler.Schedule` even decides which instances are due, held, or unread! Every held nested session, infinity sky layer, and routed world residency therefore captures its frame source (`CaptureFrame`), runs `PrepareFrame` on its emitters, packs its transforms, runs `SdfMeshPathBake.PrepareFrame`, runs `SdfMeshDresser.Dress`, packs its `SdfWorldTables`, and hashes its table signatures every single frame, even when all of its views are held by `SdfUpdateRates.Views` (30 Hz / 15 Hz / `HeldHertz`) or offscreen!
2. Inside `SdfWorldResidency.Submit` -> `m_meshIrradiance.Stage(...)`:
   - `RefreshScene` (`SdfMeshIrradiance.cs:994-1015`) iterates all `frame.MeshDraws` and hashes `draw.Mesh`, `draw.ObjectToWorld` (a 16-float `Matrix4x4`), and `draw.Material` for every static draw in the world every frame, even when `frame.MeshDrawsRevision` has not changed since the last frame!
   - `RefreshLighting` (`SdfMeshIrradiance.cs:1060-1085`) checks `Stopwatch.GetElapsedTime(startingTimestamp: m_lightingCheckedAt) < LightingInterval` at line 1084 **after** hashing all lights and calling `frame.Sky.Pack(frame.Lights, frame.FarDistance, m_skyDetails, out var block, m_skyLayers)` and hashing `block` and `m_skyLayers` (lines 1078-1082). In addition, `frame.Sky.Pack` was already called on the same `frame` in `SdfWorldTables.PackLightsAndSky`.

### Why it costs
- Preparing every `SdfWorldResidency` in `SdfWorldPasses.BeginFrame` defeats `SdfUpdateRates.Views` on the CPU: a portal, nested session, or infinity layer whose GPU render is held at 15 Hz or 0 Hz (`HeldHertz`) still runs 100% of its CPU scene emission, transform packing, mesh bake dressing, and table packing every 60 Hz frame.
- `SdfMeshIrradiance.RefreshScene` hashes thousands of `Matrix4x4` structs every frame when `frame.MeshDrawsRevision` is unchanged.
- `SdfMeshIrradiance.RefreshLighting` packs and hashes the sky and lights on every frame even during the 100 ms `LightingInterval` cooldown when it immediately returns at line 1084.

### Fix
- In `SdfWorldPasses.BeginFrame`, prepare only the boot/host residency (or residencies whose views are not held/unread), and prepare secondary residencies lazily when a view is due to be checked or rendered.
- In `SdfMeshIrradiance.RefreshScene`, cache `m_checkedDrawsRevision` against `frame.MeshDrawsRevision`; when `frame.MeshDrawsRevision == m_checkedDrawsRevision`, skip the loop over `frame.MeshDraws`.
- In `SdfMeshIrradiance.RefreshLighting`, move the `if (!frame.ConvergesLighting && (m_lighting is not null) && (Stopwatch.GetElapsedTime(startingTimestamp: m_lightingCheckedAt) < LightingInterval)) { return; }` check to the **top** of `RefreshLighting` (before packing the sky and hashing lights).

---

## 7. `WorldScreenBinder`: redundant `RebuildInfinityOutputs()` and `ReconcileViews()` calls per frame, LINQ `SelectMany` allocations, and repeated linear scans in `WorldViewSet.Set`

### Files and lines
- `src/Puck.World/WorldScreenBinder.Device.Infinity.Fit.cs:14-42`
- `src/Puck.World/WorldScreenBinder.Device.Infinity.Fit.cs:43-50`
- `src/Puck.World/WorldScreenBinder.Device.Infinity.Fit.cs:51-78`
- `src/Puck.World/WorldScreenBinder.Device.Infinity.Fit.cs:79-100`
- `src/Puck.World/WorldScreenBinder.Device.Infinity.Fit.cs:178-200`
- `src/Puck.World/WorldScreenBinder.Device.Infinity.Fit.cs:229-238`
- `src/Puck.World/WorldScreenBinder.Device.Infinity.cs:79`
- `src/Puck.World/WorldScreenBinder.Device.Infinity.cs:255-260`
- `src/Puck.World/WorldScreenBinder.Views.cs:237-320`
- `src/Puck.World.Client/Sources/WorldViewInstances.cs:268-306`

### Verbatim quotes

`src/Puck.World/WorldScreenBinder.Device.Infinity.Fit.cs:29-42`:
```csharp
        EndInfinityConsumers(target: presenter);
        // The boot world's sessions filmed into its own frame, and the cameras of the levels they show.
        BeginInfinityConsumers(target: m_homeFilms);
        foreach (var feed in m_feeds) {
            if (!feed.Home || !m_homeViewIndices.TryGetValue(key: feed.RegistrationName, value: out var index) || (((uint)index) >= ((uint)views.Count))) { continue; }
            var nested = InfinityRootOf(feed.Mirror, feed.InstanceName, feed.Depth, feed.RegistrationName);

            FitInfinity(nested, null, feed.RegistrationName, m_homeFilms, views, index, sky,
                ((uint)(feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth)),
                ((uint)(feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight)));
            FitNestedCameraSkies(feed.Nested, nested, null, m_homeFilms, views, sky);
        }
        EndInfinityConsumers(target: m_homeFilms);
    }
```

`src/Puck.World/WorldScreenBinder.Device.Infinity.Fit.cs:178-200`:
```csharp
    private void EndInfinityConsumers(object target) {
        foreach (var root in m_infinityRoots.Values) {
            m_retiredConsumers.Clear();
            foreach (var (name, consumer) in root.Consumers) {
                if (ReferenceEquals(objA: consumer.Target, objB: target) && !consumer.Seen) { m_retiredConsumers.Add(item: name); }
            }
            foreach (var name in m_retiredConsumers) { _ = root.Consumers.Remove(key: name); }
            PruneInfinityIndices(root: root);
        }
        RebuildInfinityOutputs();
        ReconcileViews();
    }
    private void RebuildInfinityOutputs() {
        m_infinityOutputs.Clear();
        foreach (var root in m_infinityRoots.Values) {
            foreach (var consumer in root.Consumers.Values) {
                foreach (var view in consumer.Views.Instances.Views) {
                    if ((PlannedOf(fit: consumer.Fit, view: view.Name) is { } planned) && root.Entries.TryGetValue(key: planned.Name, value: out var entry) && entry.Indices.TryGetValue(key: consumer.Fit.Consumer, value: out var index)) {
                        m_infinityOutputs[view.Name] = new InfinityOutput(Entry: entry, Index: index);
                    }
                }
            }
        }
    }
```

`src/Puck.World/WorldScreenBinder.Device.Infinity.cs:255-260`:
```csharp
    private IEnumerable<InfinityEntry> InfinityEntries() {
        foreach (var root in m_infinityRoots.Values) {
            if (root.Entries.Count != 0) { return m_infinityRoots.Values.SelectMany(selector: static held => held.Entries.Values); }
        }
        return [];
    }
```

`src/Puck.World.Client/Sources/WorldViewInstances.cs:268-306`:
```csharp
    public void Set(in WorldView view) {
        ArgumentException.ThrowIfNullOrEmpty(argument: view.Name);

        var insertAt = m_entries.Count;

        for (var index = 0; (index < m_entries.Count); index++) {
            var entry = m_entries[index];

            if (string.Equals(
                a: entry.View.Name,
                b: view.Name,
                comparisonType: StringComparison.Ordinal
            )) {
                entry.Seen = true;

                if (entry.View != view) {
                    m_entries.RemoveAt(index: index);
                    Set(view: in view);
                }

                return;
            }
            if (
                (insertAt == m_entries.Count) &&
                Precedes(
                    other: entry.View,
                    view: in view
                )
            ) {
                insertAt = index;
            }
        }

        m_entries.Insert(
            index: insertAt,
            item: new Entry { Seen = true, View = view }
        );
        m_changed = true;
    }
```

### What the code does each frame
1. Every time a world or session frame is captured, its sky is fit (`FitBootSky`, `FitSessionSky`, `FitRoutedSky`, `FilmInfinity`), which calls `EndInfinityConsumers(target)`.
2. `FitBootSky` alone calls `EndInfinityConsumers` **twice** per frame (`EndInfinityConsumers(target: presenter)` at line 29 and `EndInfinityConsumers(target: m_homeFilms)` at line 41); each `FitSessionSky`, `FitRoutedSky`, and `FilmInfinity` calls `EndInfinityConsumers` again; and `Publish` also calls `ReconcileInfinityRoots()` (`RebuildInfinityOutputs()`) and `ReconcileNesting()`.
3. Every call to `EndInfinityConsumers` unconditionally runs `RebuildInfinityOutputs()` and `ReconcileViews()`, even when no consumer was added, retired, or changed its `consumer.Views`!
4. Each `ReconcileViews()` call rebuilds `m_views` across all `m_cameraViews`, all `m_feeds` (calling `InfinityShown` -> `InfinityParent`, which linearly scans `m_infinityOutputs` for each feed), `SetNestedCameraViews` (which calls `InfinityEntries()`), and `SetInfinityViews()`.
5. `InfinityEntries()` (`WorldScreenBinder.Device.Infinity.cs:257`) allocates a LINQ `SelectMany` iterator (`m_infinityRoots.Values.SelectMany(selector: static held => held.Entries.Values)`) and boxes dictionary `ValueCollection.Enumerator`s on every call when any root has entries (and `InfinityEntries()` is called in `PollNestedScreens`, `RetireOrphanSessions`, `NestedSessionsOf`, `SetNestedCameraViews`, `RefreshNestedSources`, and `TryResolveNestedCamera`).
6. Inside `ReconcileViews()`, every `m_views.Set(view)` linearly scans `m_entries` with `string.Equals` (`WorldViewInstances.cs:273-300`), and when a view's struct changes (`entry.View != view`), it removes the entry and recursively calls `Set(view: in view)` to scan `m_entries` a second time.

### Why it costs
- `RebuildInfinityOutputs()` and `ReconcileViews()` run 4–10+ times per frame across boot, home films, sessions, routed scenes, and infinity entries, doing $O(V^2)$ linear scans in `WorldViewSet.Set` even when zero view definitions or extents changed.
- `InfinityEntries()` allocates a `SelectMany` state machine and boxed enumerators multiple times per frame.

### Fix
- Track a `bool changed` in `UpdateInfinityFit` (from `consumer.Views.TryPublish(instances: out _)`) and `EndInfinityConsumers` (when `m_retiredConsumers.Count > 0`), and call `RebuildInfinityOutputs()` and `ReconcileViews()` only when `changed` is `true`.
- Replace `InfinityEntries()`'s `SelectMany` with a double `foreach` over `m_infinityRoots.Values` and `root.Entries.Values` (or a cached `List<InfinityEntry>` rebuilt only in `ReplanInfinity`).

---

## 8. `WorldViewGraphHost.Rates`: repeated linear scans of `WorldScreenMappingSet` inside `ScreenSight` and `WriteScreenRateCodes` every frame

### Files and lines
- `src/Puck.World.Client/WorldViewGraphHost.Rates.cs:295-318`
- `src/Puck.World.Client/WorldViewGraphHost.Rates.cs:367-376`
- `src/Puck.World.Client/Sources/WorldScreenMappingSet.cs:296-323`

### Verbatim quotes

`src/Puck.World.Client/WorldViewGraphHost.Rates.cs:295-318`:
```csharp
        var rows = screens.Screens;

        for (var row = 0; (row < rows.Count); row++) {
            var screen = rows[row];

            if (
                !string.Equals(a: screens.InstanceOf(screen: screen.Index), b: footprint.Producer, comparisonType: StringComparison.Ordinal) ||
                !screens.TryGet(mapping: out var mapping, screen: screen.Index)
            ) {
                continue;
            }

            shown = true;

            if (!elsewhere && !WorldPortalVisibility.Sees(camera: in camera, glass: screen)) {
                continue;
            }

            var coverage = (elsewhere ? 1d : WorldPortalVisibility.Coverage(camera: in camera, glass: screen).Height);

            largest = Math.Max(val1: largest, val2: (((coverage * consumerShare) * m_displayHeight) / Math.Max(val1: 1, val2: mapping.Crop.Height)));
        }
```

`src/Puck.World.Client/Sources/WorldScreenMappingSet.cs:296-323`:
```csharp
    public string? InstanceOf(int screen) {
        for (var position = 0; (position < m_rows.Length); position++) {
            if (m_rows[position].Screen.Index == screen) {
                return Sources.InstanceOf(screen: position);
            }
        }

        return null;
    }
    /// <summary>Finds the mapping a screen last published.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <param name="mapping">The mapping when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the screen published a mapping.</returns>
    public bool TryGet(int screen, [NotNullWhen(returnValue: true)] out SourceMapping? mapping) {
        foreach (var row in m_rows) {
            if (
                (row.Screen.Index == screen) &&
                (row.Mapping is { } published)
            ) {
                mapping = published;

                return true;
            }
        }

        mapping = null;

        return false;
    }
```

`src/Puck.World.Client/WorldViewGraphHost.Rates.cs:367-376`:
```csharp
        foreach (var screen in screens.Screens) {
            if (
                (((uint)screen.Index) < ((uint)codes.Length)) &&
                (screens.InstanceOf(screen: screen.Index) is { } instance) &&
                m_ratedProducers.TryGetValue(key: instance, value: out var rated) &&
                (rated.Asked != 0)
            ) {
                codes[screen.Index] = ((byte)SdfUpdateRateTiers.CodeOf(hertz: rated.Asked, updated: rated.RenderedLatest));
            }
        }
```

### What the code does each frame
1. For every screen source footprint in `WorldViewGraphHost`, `ScreenSight` loops over all `rows = screens.Screens` (`for (var row = 0; (row < rows.Count); row++)`).
2. Inside that loop, for every screen row, it calls `screens.InstanceOf(screen: screen.Index)` and `screens.TryGet(mapping: out var mapping, screen: screen.Index)`. Both `InstanceOf` and `TryGet` on `WorldScreenMappingSet` (`src/Puck.World.Client/Sources/WorldScreenMappingSet.cs:296-323`) perform a linear scan over `m_rows` (`for (var position = 0; (position < m_rows.Length); position++)` and `foreach (var row in m_rows)`), and `Sources.InstanceOf(screen: position)` inside `WorldSourceInstances` scans `m_instances` as well.
3. With $F$ footprints and $S$ screens, `ScreenSight` performs $O(F \times S^2)$ loop iterations and string comparisons every frame, and `WriteScreenRateCodes` (`WorldViewGraphHost.Rates.cs:367-376`) performs another $O(S^2)$ scan via `screens.InstanceOf(screen: screen.Index)`.

### Why it costs
- `screens.Screens` is already in the same `position` order as `m_rows` in `WorldScreenMappingSet`, yet `ScreenSight` and `WriteScreenRateCodes` look up by `screen.Index` via linear scans over `m_rows` for every screen on every footprint.

### Fix
- Index `WorldScreenMappingSet` by screen index (or expose row access by `row` position and a producer-to-screens lookup) so `ScreenSight` iterates only the screens bound to `footprint.Producer` in $O(1)$ per bound screen.

---

## 9. `RenderGraphScheduler`, `RenderGraphRuntime`, and `ShaderPipelineRenderNode`: per-frame linear read/resource lookups, repeated footprint name lookups, and `OwnImageInput` scans

### Files and lines
- `src/Puck.Hosting/Graph/RenderGraphScheduler.cs:223-227`
- `src/Puck.Hosting/Graph/RenderGraphScheduler.cs:1002-1013`
- `src/Puck.Shaders/Graph/RenderGraphRuntime.cs:872-879`
- `src/Puck.Shaders/Graph/RenderGraphRuntime.cs:1037-1058`
- `src/Puck.Shaders/Graph/RenderGraphRuntime.ExternalReads.cs:132-136`
- `src/Puck.Shaders/ShaderPipelineRenderNode.cs:1440-1470`
- `src/Puck.Shaders/ShaderPipelineRenderNode.Packages.cs:306-352`
- `src/Puck.Shaders/ShaderPipelineRenderNode.Packages.cs:464`

### Verbatim quotes

`src/Puck.Hosting/Graph/RenderGraphScheduler.cs:1002-1013`:
```csharp
            while (grew) {
                grew = false;

                for (var position = 0; (position < footprints.Count); position++) {
                    var consumer = set.IndexOf(name: footprints[position].Consumer);
                    var producer = set.IndexOf(name: footprints[position].Producer);

                    if (named[consumer] && !named[producer]) {
                        named[producer] = true;
                        grew = true;
                    }
                }
```

`src/Puck.Shaders/Graph/RenderGraphRuntime.cs:1037-1058`:
```csharp
    private static long FrameOf(RenderGraphSchedule schedule, string consumer, string producer) {
        var reads = schedule.Reads;

        for (var read = 0; (read < reads.Count); read++) {
            var row = reads[read];

            if (
                ReferenceEquals(
                    objA: row.Consumer,
                    objB: consumer
                ) &&
                ReferenceEquals(
                    objA: row.Producer,
                    objB: producer
                )
            ) {
                return row.Frame;
            }
        }

        return -1L;
    }
```

`src/Puck.Shaders/ShaderPipelineRenderNode.cs:1440-1465`:
```csharp
    private ShaderPipelineResource? ValidateExternalBinding(string name, ShaderPipelineResourceKind kind) {
        var plan = (m_pending?.Plan ?? m_pipeline?.Plan);

        if (plan is null) {
            return null;
        }
        // An indexed loop, not a predicate or an interface enumerator: a graph instance binds its inputs on every frame,
        // and either would allocate.
        var resources = plan.Resources;

        for (var index = 0; (index < resources.Count); index++) {
            var resource = resources[index];

            if (
                string.Equals(
                    a: resource.Name,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                ) &&
                resource.Declaration.IsExternal &&
                (resource.Declaration.Kind == kind)
            ) {
                return resource.Declaration;
            }
        }
```

`src/Puck.Shaders/ShaderPipelineRenderNode.Packages.cs:333-352, 464`:
```csharp
    private bool OwnsImage(nint imageHandle) {
        foreach (var resource in m_resources) {
            if (resource?.Images is not { } images) {
                continue;
            }

            foreach (var image in images) {
                if (image?.ImageHandle == imageHandle) {
                    return true;
                }
            }
        }
        foreach (var held in m_held) {
            if ((held.Kind != ShaderPipelineResourceKind.Buffer) && !held.Leased && (held.Handle == imageHandle)) {
                return true;
            }
        }

        return false;
    }
```
```csharp
        var mayStandIn = ((pass.PackageAliasRefusal is null) && (HostInputInAnotherLayout(pass: pass, slot: slot) < 0) && (OwnImageInput(pass: pass, slot: slot) < 0));
```

### What the code does each frame
1. `RenderGraphScheduler.Schedule` runs twice per frame when `ReleaseUnnamed` releases any instance (`RenderGraphRuntime.cs:1265, 1280`). Inside `Schedule`, `Footprints` (`RenderGraphScheduler.cs:225-226`) and every iteration of the `while (grew)` fixed-point loop (`RenderGraphScheduler.cs:1005-1007`) call `set.IndexOf(name: ...)` on every footprint (`src/Puck.Hosting/Graph/RenderGraphInstanceSet.cs:331-337`) rather than reusing the `(consumer, producer)` indices already resolved in `Footprints`.
2. Before each scheduled instance renders, `RenderGraphRuntime.Bind`, `BindExternalReads`, `BindUnboundReads`, and `MarkReadStale` call `FrameOf(schedule, consumer, producer)`, which linearly scans `schedule.Reads` (`RenderGraphRuntime.cs:1040-1055`).
3. For every bound input of every rendered graph instance, `node.BindImage` / `node.BindBuffer` calls `ShaderPipelineRenderNode.ValidateExternalBinding`, which linearly scans all `plan.Resources` with `string.Equals` (`ShaderPipelineRenderNode.cs:1450-1464`), even though `m_resourceLookup` (a `Dictionary<string, RuntimeResource>`) already exists on `ShaderPipelineRenderNode`.
4. For every package pass in `ShaderPipelineRenderNode.RecordPackage` (`ShaderPipelineRenderNode.Packages.cs:464`), `mayStandIn` evaluates `OwnImageInput(pass, slot) < 0` (which calls `OwnsImage(image.ImageHandle)`, scanning all `m_resources`, all their `Images`, and `m_held`) **before** checking whether the pass can even stand in (`pass.Package is not IRenderGraphPackageReadback && pass.Package!.DrawsNothing(passBlock)`).

### Why it costs
- Repeated linear scans over `schedule.Reads`, `plan.Resources`, and `m_resources`, plus repeated dictionary lookups in `while (grew)`, run across every instance, binding, and package pass every frame.

### Fix
- Resolve `(consumer, producer)` footprint indices once in `Footprints` and reuse them in the `while (grew)` loop.
- Store the read index or resolved frame directly by `(consumerIndex, readEdgeIndex)` in `RenderGraphSchedule` instead of scanning `schedule.Reads` in `FrameOf`.
- Use `m_resourceLookup` in `ShaderPipelineRenderNode.ValidateExternalBinding` (when `m_pending is null`) instead of scanning `plan.Resources`.
- In `ShaderPipelineRenderNode.RecordPackage`, compute `mayStandIn` lazily only for passes where `pass.PackageAliasRefusal is null` and the package actually reads `MayStandIn` or `DrawsNothing(passBlock)` returns `true`.

---

## 10. `WorldGpuTiming.Tick`: redundant loop over all render-graph instances every frame when GPU timing is enabled

### File and lines
- `src/Puck.World/WorldGpuTiming.cs:35-52`

### Verbatim quote

`src/Puck.World/WorldGpuTiming.cs:35-52`:
```csharp
    public void Tick() {
        var renewed = (m_demand != m_applied);

        if (!Enabled && !renewed) { return; }
        m_applied = m_demand;
        Apply(renewed: renewed);
    }

    private void Apply(bool renewed) {
        if (probe?.Root?.Runtime is not { } runtime) { return; }
        for (var index = 0; (index < runtime.Instances.Instances.Count); index++) {
            if (runtime.Producer(instance: index) is not null) { continue; }
            var node = runtime.Node(instance: index);

            if (renewed) { node.TimingEnabled = false; }
            node.TimingEnabled = Enabled;
        }
    }
```

### What the code does each frame
Called at the top of `WorldRenderRoot.ProduceFrame` (`src/Puck.World/WorldRenderRoot.cs:328`), `WorldGpuTiming.Tick()` checks `if (!Enabled && !renewed) { return; }`. Whenever `Enabled` is `true` (such as when dynamic resolution is active), `Tick()` calls `Apply(renewed: false)` on **every frame**, looping over all `runtime.Instances.Instances.Count` to set `node.TimingEnabled = true` on nodes where `TimingEnabled` is already `true`.

### Why it costs
- Unnecessary per-frame iteration over all render-graph instances when neither `m_demand` nor `probe.Root.Runtime` changed.

### Fix
- Track the last `RenderGraphRuntime` instance `m_appliedRuntime` alongside `m_applied`; return early in `Tick()` when `!renewed && ReferenceEquals(runtime, m_appliedRuntime)`.
