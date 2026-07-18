# SDF backlog

This page contains open SDF work only. Shipped capabilities belong in the
[capability catalog](capability-catalog.md), technical contracts in the
[SDF handbook](sdf-handbook/README.md) and `sdf-world` skill, and benchmark
procedure in [sdf-bench-notes.md](sdf-bench-notes.md).

Item numbers are stable references used by code comments and related
documentation. Do not renumber them.

## Performance

1. **Within-instance segment pruning.** Derive per-segment influence bounds
   when baking placed creations. Whole-instance grid culling is already the
   outer tier; this item targets long multi-segment programs inside one
   placement.

2. **Segment tracing.** Reconsider only after item 1 shows that bake-time
   segment bounds leave a material cost. Do not add a second hierarchy without
   a representative workload and paired GPU measurements.

3. **March/shade split.** Evaluate a separate material-resolution path only if
   it reduces the views kernel's live state without adding more field walks.

5. **Per-tile or per-segment Lipschitz refinement.** Replace the program-wide
   worst-case factor only when the refinement remains conservative across
   folds, scopes, and dynamic instances.

7. **Beam alternatives.** Reopen only for a workload in which beam time again
   dominates views time. The current mask-first grid is the default.

## Quality and shading

9. **Ray-differential CRT filtering.** Reopen when screen-source minification
   is a visible defect in ordinary play.

11. **Cone AO or bent-normal tier.** Treat as an optional quality tier. It must
    preserve the default three-tap AO path and be measured against the same
    camera and lighting state.

12. **Bound-preserving fBm operation.** Requires an integer-hash basis, a
    conservative derivative bound, and cross-backend parity evidence.

13. **Coverage-AA continuation.** Pursue only for a demonstrated silhouette
    defect that the existing footprint-adaptive termination does not cover.

14. **Curvature-guided stepping.** Requires a conservative curvature estimate
    whose cost is lower than the steps it removes.

## Authoring and ISA

18. **Field-scope depth greater than one.** Convert the shader's scalar
    `SdfFieldSave` slot and the builder's single open-scope state to indexed
    stacks before raising `MaxFieldScopeDepth`.

20. **Additional lifted 2D primitives.** Evaluate arc/pie, cross, moon, egg,
    and heart shapes against the ISA admission rule. Prefer exact builder
    composition when available.

21. **Diegetic UI convergence.** Keep dense reading text on `GlyphDecal` and
    marchable labels on `Glyph`; add shared authoring tools rather than a third
    rendering tier.

26. **Surface chart.** Opcode value 19 remains reserved. Admit a chart
    operation only with a concrete authoring use and a complete C#↔HLSL lane
    contract.

29. **Log-spherical camera framing.** Document and enforce an authoring-safe
    framing rule if ordinary content can cross shell boundaries in a way that
    destabilizes presentation.

## Queries and gravity

30. **Analytic gradient magnitude.** `SdfFieldEvaluator.TryFieldGradient`
    returns a normalized direction. Add a separate raw-gradient API only for a
    consumer that needs magnitude.

31. **`RepeatPolar` in `SdfFieldEvaluator`.** Add a fixed-point angular fold
    and validate sector-boundary behavior against the shader.

32. **`WallpaperFold` in `SdfFieldEvaluator`.** Port all wallpaper groups as
    one synchronized unit; partial group support is not acceptable.

33. **Gravity takeover exclusion.** Define how gravity-walker control excludes
    or coexists with other player-control modes.

34. **Gravity walker lifecycle verbs.** Expose the existing clear/despawn
    capability through the live control plane.

35. **Replay recording shape.** Record live player intent in a deterministic,
    per-tick form suitable for replay.

36. **Player-facing standard camera name.** Choose a stable public name for
    the oriented follow camera before exposing it through authoring data.

## Integration and maintenance

27. **Shared emission seam.** Consolidate duplicated creator and companion
    shape emission without introducing a demo dependency into `Puck.SdfVm`.

28. **Render assembly follow-ups.** Add an `IRenderNode` for live-camera
    viewport sources, host cross-backend world production, and share the
    stale-bytecode guard with other shader-owning projects where appropriate.

37. **Sampled-region verification policy.** Keep the sampled-region engine
    contract covered by the normal Post battery and retire any special
    informational-only routing.

## Known defect

23. **Ground-plane horizon notch.** A grazing horizon can expose tile-quantized
    gaps. Reproduce with a stable camera, then determine whether the fault is
    in beam coverage, tile bounds, or footprint termination before changing
    thresholds.
