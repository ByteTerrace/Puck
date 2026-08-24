using Puck.Maths;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateAdjacencies(WorldDefinition definition, HashSet<string> destinationNames, IWorldNeighbourResolver? neighbours, bool proveNeighbours, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var resolutions = new Dictionary<string, WorldNeighbourResolution>(comparer: StringComparer.Ordinal);
        var channels = WorldChannelTable.Compile(channels: definition.Channels);

        foreach (var adjacency in (definition.Adjacencies ?? [])) {
            if (adjacency is null) {
                errors.Add(item: "adjacencies contains a null row.");
                continue;
            }

            var path = $"adjacencies[{adjacency.Name}]";

            if (!names.Add(item: adjacency.Name.Value)) {
                errors.Add(item: $"{path}.name is duplicated.");
            }
            if (
                !destinationNames.Contains(item: adjacency.Destination) ||
                (WorldDefinitionRows.FindDestination(
                destinations: definition.Destinations,
                name: adjacency.Destination
            ) is not { } destination)
            ) {
                errors.Add(item: $"{path}.destination '{adjacency.Destination}' names no destinations row.");
                continue;
            }
            if (
                (destination.Scope != WorldDestinationScope.Global) ||
                (destination.Durability != WorldDestinationDurability.Persisted)
            ) {
                errors.Add(item: $"{path}.destination '{destination.Name}' must be global and persisted — adjacency names one stable neighbouring authority.");
            }
            if (!WorldSafeName.TryParse(
                candidate: adjacency.Counterpart,
                name: out _,
                reason: out var counterpartReason
            )) {
                errors.Add(item: $"{path}.counterpart '{adjacency.Counterpart}' is invalid — {counterpartReason}.");
            }
            if (
                (adjacency.Boundary is not { } boundary) ||
                !IsFinite(value: boundary.Center) ||
                !float.IsFinite(f: boundary.OutwardYawDegrees) ||
                !float.IsFinite(f: boundary.OutwardPitchDegrees) ||
                !float.IsFinite(f: boundary.Width) ||
                (boundary.Width <= 0f) ||
                !float.IsFinite(f: boundary.Height) ||
                (boundary.Height <= 0f)
            ) {
                errors.Add(item: $"{path}.boundary must have a finite center/yaw/pitch and positive finite width/height.");
                continue;
            }
            if (!Enum.IsDefined(value: adjacency.Unavailable)) {
                errors.Add(item: $"{path}.unavailable '{adjacency.Unavailable}' is not defined.");
            }
            if (
                (adjacency.OnUnavailable is { } onUnavailable) &&
                (string.IsNullOrWhiteSpace(value: onUnavailable) || !channels.TryGetOrdinal(
                name: onUnavailable,
                ordinal: out _
            ))
            ) {
                errors.Add(item: $"{path}.onUnavailable '{onUnavailable}' names no declared channel.");
            }
            if (adjacency.Capacity is { } borderCapacity) {
                RequireIntRange(
                    errors: errors,
                    max: WorldPopulationLimits.CapacityCeiling,
                    min: 1,
                    name: $"{path}.capacity",
                    value: borderCapacity
                );
            }
            // The same 0..600 band population.reconnectGraceSeconds carries: 0 disables, and a window past ten
            // minutes has stopped being a liveness signal.
            RequireRange(
                value: adjacency.LivenessGraceSeconds,
                min: 0f,
                max: 600f,
                name: $"{path}.livenessGraceSeconds",
                errors: errors
            );
            if (!proveNeighbours) {
                continue;
            }
            if (WorldDefinitionRows.FindReference(
                references: definition.References,
                name: destination.Reference
            ) is not { } reference) {
                continue;
            }
            if (neighbours is null) {
                errors.Add(item: $"{path} cannot be proven because no neighbour resolver was supplied.");
                continue;
            }
            if (!resolutions.TryGetValue(
                key: reference.NeighbourKey,
                value: out var resolution
            )) {
                resolution = neighbours.Resolve(document: reference.NeighbourKey);
                resolutions[reference.NeighbourKey] = resolution;
            }
            // A verified attestation carries the same shape an ordinary unverified one does (and strictly more —
            // a bound, authenticated subject), so it proves an ordinary two-document adjacency exactly the same way.
            if (
                (resolution.Kind == WorldNeighbourResolutionKind.Attested) ||
                (resolution.Kind == WorldNeighbourResolutionKind.VerifiedAttested)
            ) {
                ValidateAttestedCounterpart(
                    path: path,
                    definition: definition,
                    adjacency: adjacency,
                    document: reference.NeighbourKey,
                    attestation: resolution.Attestation!,
                    boundary: boundary,
                    errors: errors
                );
                continue;
            }

            if (resolution.Kind != WorldNeighbourResolutionKind.Resolved) {
                errors.Add(item: $"{path} cannot reach neighbour '{reference.NeighbourKey}' — {resolution.Reason}.");
                continue;
            }

            if (resolution.Definition is not { } neighbour) {
                errors.Add(item: $"{path} resolver returned no neighbour document for '{reference.NeighbourKey}'.");
                continue;
            }
            if (WorldDefinitionRows.FindAdjacency(
                adjacencies: neighbour.Adjacencies,
                name: adjacency.Counterpart
            ) is not { } counterpart) {
                errors.Add(item: $"{path}.counterpart '{adjacency.Counterpart}' names no adjacency in neighbour '{reference.NeighbourKey}'.");
                continue;
            }
            if (!string.Equals(
                a: counterpart.Counterpart,
                b: adjacency.Name.Value,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"{path} is not reciprocal — neighbour '{reference.NeighbourKey}'/'{counterpart.Name}' points to '{counterpart.Counterpart}', not '{adjacency.Name}'.");
            }

            var localFrame = boundary.CompileFrame();

            if (counterpart.Boundary is not { } counterpartBoundary) {
                errors.Add(item: $"{path}.counterpart '{adjacency.Counterpart}' has no boundary.");
                continue;
            }
            var neighbourFrame = counterpartBoundary.CompileFrame();

            if (
                (localFrame.HalfWidth != neighbourFrame.HalfWidth) ||
                (localFrame.HalfHeight != neighbourFrame.HalfHeight)
            ) {
                errors.Add(item: $"{path}.boundary is {(((double)localFrame.HalfWidth) * 2):0.#####}x{(((double)localFrame.HalfHeight) * 2):0.#####}, but neighbour '{reference.NeighbourKey}'/'{counterpart.Name}' is {(((double)neighbourFrame.HalfWidth) * 2):0.#####}x{(((double)neighbourFrame.HalfHeight) * 2):0.#####}.");
            }
            var worldUp = new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            );

            if (WorldFrameIsometry.MapVector(
                destination: neighbourFrame,
                source: localFrame,
                value: worldUp
            ) != worldUp) {
                errors.Add(item: $"{path}.boundary and neighbour '{reference.NeighbourKey}'/'{counterpart.Name}' do not preserve world up — body yaw/vertical state cannot cross this frame pair without loss.");
            }
            if (!WorldAdjacencyPolicy.TryDeriveOverlap(
                depth: out _,
                local: definition,
                neighbour: neighbour,
                reason: out var overlapReason
            )) {
                errors.Add(item: $"{path} overlap cannot be derived — {overlapReason}.");
            }
        }

        if (
            proveNeighbours &&
            (neighbours is not null)
        ) {
            ValidateDerivedAdjacencyCorners(
                definition: definition,
                errors: errors,
                neighbours: neighbours,
                resolutions: resolutions
            );
        }
    }
    private static void ValidateCornerDiamond(
        string path,
        WorldAdjacency leftSourceEdge,
        WorldAdjacencyDocumentView left,
        WorldAdjacencyEdgeView leftCornerEdge,
        WorldAdjacency rightSourceEdge,
        WorldAdjacencyDocumentView right,
        WorldAdjacencyEdgeView rightCornerEdge,
        string cornerDocument,
        WorldAdjacencyDocumentView corner,
        List<string> errors
    ) {
        if (
            (left.FindEdge(name: leftSourceEdge.Counterpart) is not { } leftBack) ||
            (right.FindEdge(name: rightSourceEdge.Counterpart) is not { } rightBack) ||
            (corner.FindEdge(name: leftCornerEdge.Counterpart) is not { } cornerToLeft) ||
            (corner.FindEdge(name: rightCornerEdge.Counterpart) is not { } cornerToRight)
        ) {
            return;
        }

        var cornerOrigin = cornerToLeft.Boundary.CompileFrame().Origin;
        var probes = new[] {
            cornerOrigin,
            (cornerOrigin + new FixedVector3(
            X: FixedQ4816.One,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        )),
            (cornerOrigin + new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.One
        )),
        };

        foreach (var probe in probes) {
            var viaLeft = MapTwoStages(
                point: probe,
                firstSource: cornerToLeft.Boundary.CompileFrame(),
                firstDestination: leftCornerEdge.Boundary.CompileFrame(),
                secondSource: leftBack.Boundary.CompileFrame(),
                secondDestination: leftSourceEdge.Boundary.CompileFrame()
            );
            var viaRight = MapTwoStages(
                point: probe,
                firstSource: cornerToRight.Boundary.CompileFrame(),
                firstDestination: rightCornerEdge.Boundary.CompileFrame(),
                secondSource: rightBack.Boundary.CompileFrame(),
                secondDestination: rightSourceEdge.Boundary.CompileFrame()
            );

            if (viaLeft != viaRight) {
                errors.Add(item: $"{path} does not close its transform diamond for corner '{cornerDocument}' — the left path maps {probe} to {viaLeft}, while the right path maps it to {viaRight}.");
                return;
            }
        }
    }
    private static void ValidateCornerPath(string path, string viaDocument, WorldAdjacencyDocumentView via, WorldAdjacencyEdgeView viaEdge, string cornerDocument, WorldAdjacencyDocumentView corner, List<string> errors) {
        if (corner.FindEdge(name: viaEdge.Counterpart) is not { } counterpart) {
            errors.Add(item: $"{path} reaches '{cornerDocument}' through '{viaDocument}'/'{viaEdge.Name}', but corner counterpart '{viaEdge.Counterpart}' does not exist.");
            return;
        }

        if (!string.Equals(
            a: counterpart.Counterpart,
            b: viaEdge.Name.Value,
            comparisonType: StringComparison.Ordinal
        )) {
            errors.Add(item: $"{path} reaches '{cornerDocument}' through '{viaDocument}'/'{viaEdge.Name}', but corner '{counterpart.Name}' points to '{counterpart.Counterpart}', not '{viaEdge.Name}'.");
        }

        var viaFrame = viaEdge.Boundary.CompileFrame();
        var cornerFrame = counterpart.Boundary.CompileFrame();

        if (
            (viaFrame.HalfWidth != cornerFrame.HalfWidth) ||
            (viaFrame.HalfHeight != cornerFrame.HalfHeight)
        ) {
            errors.Add(item: $"{path} reaches '{cornerDocument}' through '{viaDocument}'/'{viaEdge.Name}', whose boundary dimensions do not match corner '{counterpart.Name}'.");
        }

        if (
            !via.TryOverlapTerms(
            reason: out var viaReason,
            terms: out var viaTerms
        ) ||
            (viaTerms is null)
        ) {
            errors.Add(item: $"{path} derived corner overlap cannot be compiled — {viaReason}.");
            return;
        }
        if (
            !corner.TryOverlapTerms(
            reason: out var cornerReason,
            terms: out var cornerTerms
        ) ||
            (cornerTerms is null)
        ) {
            errors.Add(item: $"{path} derived corner overlap cannot be compiled — {cornerReason}.");
            return;
        }
        if (!WorldAdjacencyPolicy.TryDeriveOverlap(
            depth: out _,
            local: viaTerms,
            neighbour: cornerTerms,
            reason: out var overlapReason
        )) {
            errors.Add(item: $"{path} derived corner overlap cannot be compiled — {overlapReason}.");
        }
    }
    private static void ValidateDerivedAdjacencyCorners(
        WorldDefinition definition,
        IWorldNeighbourResolver neighbours,
        Dictionary<string, WorldNeighbourResolution> resolutions,
        List<string> errors
    ) {
        var rows = (definition.Adjacencies ?? []).Where(predicate: static row => (row is not null)).ToArray();

        for (var leftIndex = 0; (leftIndex < rows.Length); leftIndex++) {
            var left = rows[leftIndex]!;

            if (
                (WorldAdjacencyPolicy.DestinationNeighbourKey(
                definition: definition,
                destinationName: left.Destination
            ) is not { } leftDocument) ||
                !TryResolvedView(
                document: leftDocument,
                neighbours: neighbours,
                resolutions: resolutions,
                view: out var leftView
            )
            ) {
                continue;
            }

            for (var rightIndex = (leftIndex + 1); (rightIndex < rows.Length); rightIndex++) {
                var right = rows[rightIndex]!;

                if (
                    (WorldAdjacencyPolicy.DestinationNeighbourKey(
                    definition: definition,
                    destinationName: right.Destination
                ) is not { } rightDocument) ||
                    !TryResolvedView(
                    document: rightDocument,
                    neighbours: neighbours,
                    resolutions: resolutions,
                    view: out var rightView
                ) ||
                    !WorldAdjacencyPolicy.TrySharedCorner(
                    left: leftView,
                    leftBack: left.Counterpart,
                    right: rightView,
                    rightBack: right.Counterpart,
                    document: out var cornerDocument,
                    leftEdge: out var leftEdge,
                    rightEdge: out var rightEdge
                )
                ) {
                    continue;
                }

                var path = $"adjacencies[{left.Name}]+adjacencies[{right.Name}]";

                if (WorldAdjacencyPolicy.GlobalDestinationForNeighbourKey(
                    definition: definition,
                    neighbourKey: cornerDocument
                ) is null) {
                    errors.Add(item: $"{path} derives corner neighbour '{cornerDocument}', but this document declares no global persisted destination/reference for that authority.");
                    continue;
                }

                if (!TryResolvedView(
                    document: cornerDocument,
                    neighbours: neighbours,
                    resolutions: resolutions,
                    view: out var cornerView
                )) {
                    var resolution = resolutions[cornerDocument];

                    errors.Add(item: $"{path} cannot reach derived corner neighbour '{cornerDocument}' — {DescribeUnprovenCornerNeighbour(resolution: resolution)}.");
                    continue;
                }

                ValidateCornerPath(
                    corner: cornerView,
                    cornerDocument: cornerDocument,
                    errors: errors,
                    path: path,
                    via: leftView,
                    viaDocument: leftDocument,
                    viaEdge: leftEdge
                );
                ValidateCornerPath(
                    corner: cornerView,
                    cornerDocument: cornerDocument,
                    errors: errors,
                    path: path,
                    via: rightView,
                    viaDocument: rightDocument,
                    viaEdge: rightEdge
                );
                ValidateCornerDiamond(
                    corner: cornerView,
                    cornerDocument: cornerDocument,
                    errors: errors,
                    left: leftView,
                    leftCornerEdge: leftEdge,
                    leftSourceEdge: left,
                    path: path,
                    right: rightView,
                    rightCornerEdge: rightEdge,
                    rightSourceEdge: right
                );
            }
        }
    }
    // The refusal text for a neighbour the corner walk could not use: Unavailable names its own reason; a plain
    // Attested arm carries none (it is not a failure to reach the neighbour, it is a proof the corner walk does not
    // accept) so this names why in its place. Resolved/VerifiedAttested reach here only when the resolution's own
    // payload (Definition/Attestation) is missing despite the Kind claiming otherwise, which also carries no reason
    // to fall back on — named explicitly rather than left to a blank interpolation. Every named Kind is listed by
    // hand so a future arm cannot fall through the discard and read as covered when it was never considered.
    private static string DescribeUnprovenCornerNeighbour(WorldNeighbourResolution resolution) => (resolution.Kind switch {
        WorldNeighbourResolutionKind.Attested => "the neighbour attested this seam without a verified claim binding an authenticated subject to the document — a derived corner requires a resolved document or a verified attestation",
        WorldNeighbourResolutionKind.Resolved or WorldNeighbourResolutionKind.VerifiedAttested => $"the resolver returned a {resolution.Kind} outcome carrying no payload",
        WorldNeighbourResolutionKind.Unavailable => resolution.Reason,
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(resolution),
        actualValue: resolution.Kind,
        message: "unrecognized neighbour resolution kind"
    ),
    });
    private static FixedVector3 MapTwoStages(FixedVector3 point, WorldFaceFrame firstSource, WorldFaceFrame firstDestination, WorldFaceFrame secondSource, WorldFaceFrame secondDestination) {
        var intermediate = WorldFrameIsometry.MapPoint(
            destination: in firstDestination,
            point: point,
            source: in firstSource
        );

        return WorldFrameIsometry.MapPoint(
            destination: in secondDestination,
            point: intermediate,
            source: in secondSource
        );
    }
    // The derived-corner walk's own resolution: a corner names a third authority, so it accepts only a whole document
    // or a signed claim whose chain-of-trust bound an authenticated subject to exactly that document
    // (WorldNeighbourResolutionKind.VerifiedAttested) — never a plain, unverified WorldNeighbourResolutionKind.Attested,
    // which proves an ordinary two-document adjacency (ValidateAttestedCounterpart) but nothing about a third party.
    private static bool TryResolvedView(
        string document,
        IWorldNeighbourResolver neighbours,
        Dictionary<string, WorldNeighbourResolution> resolutions,
        out WorldAdjacencyDocumentView view
    ) {
        if (!resolutions.TryGetValue(
            key: document,
            value: out var resolution
        )) {
            resolution = neighbours.Resolve(document: document);
            resolutions[document] = resolution;
        }

        if (
            (resolution.Kind == WorldNeighbourResolutionKind.Resolved) &&
            (resolution.Definition is { } definition)
        ) {
            view = WorldAdjacencyDocumentView.FromDefinition(definition: definition);
            return true;
        }
        if (
            (resolution.Kind == WorldNeighbourResolutionKind.VerifiedAttested) &&
            (resolution.Attestation is { } attestation)
        ) {
            view = WorldAdjacencyDocumentView.FromAttestation(attestation: attestation);
            return true;
        }

        view = default;
        return false;
    }
    // The same four per-fact proofs the resolved-document arm makes, from the counterpart's attested edges alone —
    // an ordinary two-document adjacency only. A derived corner is proven the same way from all three documents
    // (see ValidateDerivedAdjacencyCorners), but never from a plain, unverified attestation: a corner is a claim
    // about a third authority, so it requires either that authority's own document or a signed claim whose
    // chain-of-trust bound an authenticated subject to exactly that document (WorldNeighbourResolutionKind.VerifiedAttested).
    private static void ValidateAttestedCounterpart(
        string path,
        WorldDefinition definition,
        WorldAdjacency adjacency,
        string document,
        WorldCounterpartAttestation attestation,
        WorldAdjacencyBoundary boundary,
        List<string> errors
    ) {
        if (!string.Equals(
            a: attestation.Document,
            b: document,
            comparisonType: StringComparison.Ordinal
        )) {
            errors.Add(item: $"{path} attestation names document '{attestation.Document}', not '{document}'.");
            return;
        }

        if (attestation.FindEdge(name: adjacency.Counterpart) is not { } counterpart) {
            errors.Add(item: $"{path}.counterpart '{adjacency.Counterpart}' names no adjacency in neighbour '{document}'.");
            return;
        }

        if (!string.Equals(
            a: counterpart.Counterpart,
            b: adjacency.Name.Value,
            comparisonType: StringComparison.Ordinal
        )) {
            errors.Add(item: $"{path} is not reciprocal — neighbour '{document}'/'{counterpart.Name}' points to '{counterpart.Counterpart}', not '{adjacency.Name}'.");
        }

        var localFrame = boundary.CompileFrame();
        var neighbourFrame = counterpart.Boundary.CompileFrame();

        if (
            (localFrame.HalfWidth != neighbourFrame.HalfWidth) ||
            (localFrame.HalfHeight != neighbourFrame.HalfHeight)
        ) {
            errors.Add(item: $"{path}.boundary is {(((double)localFrame.HalfWidth) * 2):0.#####}x{(((double)localFrame.HalfHeight) * 2):0.#####}, but neighbour '{document}'/'{counterpart.Name}' is {(((double)neighbourFrame.HalfWidth) * 2):0.#####}x{(((double)neighbourFrame.HalfHeight) * 2):0.#####}.");
        }

        var worldUp = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.One,
            Z: FixedQ4816.Zero
        );

        if (WorldFrameIsometry.MapVector(
            destination: neighbourFrame,
            source: localFrame,
            value: worldUp
        ) != worldUp) {
            errors.Add(item: $"{path}.boundary and neighbour '{document}'/'{counterpart.Name}' do not preserve world up — body yaw/vertical state cannot cross this frame pair without loss.");
        }

        if (!WorldAdjacencyPolicy.TryDeriveOverlap(
            local: definition,
            neighbour: attestation.Overlap,
            depth: out _,
            reason: out var overlapReason
        )) {
            errors.Add(item: $"{path} overlap cannot be derived — {overlapReason}.");
        }
    }
}
