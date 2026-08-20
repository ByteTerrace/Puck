using Puck.Maths;

namespace Puck.SignedDistance;

/// <summary>
/// Answers an ordered <see cref="SdfDomainOp"/> chain as the finite set of rigid copies it generates, for the consumers
/// that need a fold's result as placed geometry rather than as a point transform — analytic contact compilation, bound
/// enumeration, anything that places a copy instead of evaluating a field.
/// </summary>
/// <remarks>
/// <para>A fold maps many points onto one fundamental domain, so the solid it produces is the preimage of the authored
/// shape: for an isometric fold, the union of that shape under each branch isometry. Ops compose in the order the point
/// visits them, so op 1's branches sit outermost — for the chain <c>f1, f2</c> the copies are <c>g1 ∘ g2</c> over both
/// branch sets.</para>
/// <para>Exact only when the authored shape lies inside the fold's fundamental domain: wholly on a symmetry plane's
/// positive side, inside a repeat's centre cell, between a polar sector's walls. The fold clips a shape that straddles
/// a wall; the expansion keeps it whole, so the expansion over-reports. A caller that must not over-report checks
/// containment itself.</para>
/// <para><see cref="SdfDomainOp.Wallpaper"/> has no expansion: its groups fold by a parity-keyed point group with no
/// rigid-copy spelling here. It refuses by name.</para>
/// </remarks>
public static class SdfDomainExpansion {
    /// <summary>The default ceiling on the copies one chain may generate.</summary>
    /// <remarks>Sized for the analytic contact compiler, which has no broadphase: every copy becomes a collider tested
    /// against every body, every tick.</remarks>
    public const int DefaultCopyBudget = 64;

    private static readonly FixedQ4816 MinimumSpacing = FixedQ4816.FromDouble(value: 0.001);
    private static readonly FixedQ4816 Two = FixedQ4816.FromInteger(value: 2L);
    private static readonly FixedVector3 UnitX = new(
        X: FixedQ4816.One,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.Zero
    );

    // The fold plane's (u, v) world-axis indices, the untouched axial index, and the sign carrying a +angle rotation
    // from u toward v into a right-handed rotation about the axis: (x, z) is left-handed about +Y, the other two pairs
    // right-handed. KEEP IN SYNC with SDF_OP_REPEAT_POLAR's plane selection in Assets/Shaders/Sdf/sdf-vm.hlsli.
    private static (int U, int V, int W, double Sign) PlaneAxes(SdfPolarAxis axis) {
        return axis switch {
            SdfPolarAxis.X => (U: 1, V: 2, W: 0, Sign: 1d),
            SdfPolarAxis.Z => (U: 0, V: 1, W: 2, Sign: 1d),
            _ => (U: 0, V: 2, W: 1, Sign: -1d),
        };
    }
    private static FixedVector3 UnitAxis(int index) {
        return index switch {
            0 => new FixedVector3(
            X: FixedQ4816.One,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        ),
            1 => new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.One,
            Z: FixedQ4816.Zero
        ),
            _ => new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.One
        ),
        };
    }
    // The product of the two reflections H(a)H(b) as a unit quaternion (b × a, b · a) — the integer-only route from a
    // mirror to the proper rotation left once H(x̂) is factored out, with no matrix-to-quaternion reconstruction and so
    // no platform sqrt.
    private static FixedQuaternion MirrorRemainder(FixedVector3 unitNormal) {
        var vector = FixedVector3.Cross(
            left: UnitX,
            right: unitNormal
        );

        return new FixedQuaternion(
            W: FixedVector3.Dot(
                left: UnitX,
                right: unitNormal
            ),
            X: vector.X,
            Y: vector.Y,
            Z: vector.Z
        ).Normalize();
    }
    // A repeat limit reaches a copy set as a cell COUNT, so it must be whole: the shader's
    // clamp(round(p / spacing), -limit, limit) parks everything past a fractional limit at that fractional offset,
    // which is a copy off the lattice rather than one more lattice cell.
    private static bool TryCellLimit(float limit, out int cells) {
        cells = 0;

        if (
            !float.IsFinite(f: limit) ||
            (limit < 0f) ||
            (limit != MathF.Truncate(x: limit)) ||
            (limit > 1024f)
        ) {
            return false;
        }

        cells = ((int)limit);

        return true;
    }
    // The budget is judged against the count a branch set would have, in closed form, before one frame exists: the
    // authored values reaching here are hostile-document scale — a repeat limit of 120 is 14 million frames and any
    // limit at or past 645 exceeds Array.MaxLength — so a refusal that costs what it refuses is not a refusal.
    // Both generators know their count exactly: (2l_x+1)(2l_y+1)(2l_z+1) cells, count·(mirror ? 2 : 1) sectors.
    // The arithmetic is bounded by construction — TryCellLimit caps each axis at 1024, so the repeat product is at
    // most 2049^3, and a polar's doubled int count is at most 2^32; both sit far inside long.
    private static bool WithinBudget(long accumulated, long branchCount, int copyBudget, string opName, out string refusal) {
        if ((accumulated * branchCount) > copyBudget) {
            refusal = $"a domain chain whose {opName} op expands it to {(accumulated * branchCount)} copies, past the {copyBudget}-copy budget";

            return false;
        }

        refusal = string.Empty;

        return true;
    }
    private static bool TryBranches(SdfDomainOp op, long accumulated, int copyBudget, List<SdfRigidFrame> branches, out string refusal) {
        refusal = string.Empty;

        branches.Clear();

        switch (op) {
            case SdfDomainOp.Symmetry symmetry: {
                    var normal = FixedVector3.FromVector3(value: symmetry.Normal).Normalize();

                    if (normal == FixedVector3.Zero) {
                        refusal = "a symmetry domain op whose plane normal is zero";

                        return false;
                    }

                    if (!WithinBudget(
                        accumulated: accumulated,
                        branchCount: 2L,
                        copyBudget: copyBudget,
                        opName: "symmetry",
                        refusal: out refusal
                    )) {
                        return false;
                    }

                    branches.Add(item: SdfRigidFrame.Identity);
                    branches.Add(item: new SdfRigidFrame(
                        Mirrored: true,
                        Position: ((normal * FixedQ4816.FromDouble(value: symmetry.Offset)) * -Two),
                        Rotation: MirrorRemainder(unitNormal: normal)
                    ));

                    return true;
                }
            case SdfDomainOp.Repeat repeat: {
                    if (
                        !TryCellLimit(
                        cells: out var limitX,
                        limit: repeat.Limit.X
                    ) ||
                        !TryCellLimit(
                        cells: out var limitY,
                        limit: repeat.Limit.Y
                    ) ||
                        !TryCellLimit(
                        cells: out var limitZ,
                        limit: repeat.Limit.Z
                    )
                    ) {
                        refusal = "a repeat domain op whose cell limit is not a whole number in [0, 1024]";

                        return false;
                    }

                    // The degenerate-spacing clamp is taken WITHOUT Abs, matching SdfProgramBuilder.RepeatLimited's own
                    // max(spacing, 0.001): a negative spacing folds as it always has.
                    var spacing = new FixedVector3(
                        X: FixedQ4816.Max(
                            x: FixedQ4816.FromDouble(value: repeat.Spacing.X),
                            y: MinimumSpacing
                        ),
                        Y: FixedQ4816.Max(
                            x: FixedQ4816.FromDouble(value: repeat.Spacing.Y),
                            y: MinimumSpacing
                        ),
                        Z: FixedQ4816.Max(
                            x: FixedQ4816.FromDouble(value: repeat.Spacing.Z),
                            y: MinimumSpacing
                        )
                    );

                    if (!WithinBudget(
                        accumulated: accumulated,
                        branchCount: ((((2L * limitX) + 1L) * ((2L * limitY) + 1L)) * ((2L * limitZ) + 1L)),
                        copyBudget: copyBudget,
                        opName: "repeat",
                        refusal: out refusal
                    )) {
                        return false;
                    }

                    for (var cellX = -limitX; (cellX <= limitX); cellX++) {
                        for (var cellY = -limitY; (cellY <= limitY); cellY++) {
                            for (var cellZ = -limitZ; (cellZ <= limitZ); cellZ++) {
                                branches.Add(item: SdfRigidFrame.Identity with {
                                    Position = new FixedVector3(
                                        X: (spacing.X * FixedQ4816.FromInteger(value: cellX)),
                                        Y: (spacing.Y * FixedQ4816.FromInteger(value: cellY)),
                                        Z: (spacing.Z * FixedQ4816.FromInteger(value: cellZ))
                                    ),
                                });
                            }
                        }
                    }

                    return true;
                }
            case SdfDomainOp.Polar polar: {
                    var (_, v, w, sign) = PlaneAxes(axis: polar.Axis);
                    var axis = UnitAxis(index: w);
                    var count = Math.Max(
                        val1: polar.Count,
                        val2: 1
                    );
                    // The sector fold rotates a point by -angle·sector, so its branches are the +angle·sector rotations.
                    var mirrorRemainder = MirrorRemainder(unitNormal: UnitAxis(index: v));

                    if (!WithinBudget(
                        accumulated: accumulated,
                        branchCount: (((long)count) * (polar.Mirror
                        ? 2L
                        : 1L)),
                        copyBudget: copyBudget,
                        opName: "polar",
                        refusal: out refusal
                    )) {
                        return false;
                    }

                    for (var sector = 0; (sector < count); sector++) {
                        var rotation = FixedQuaternion.FromAxisAngle(
                            angle: FixedQ4816.FromDouble(value: ((sign * ((2d * Math.PI) * sector)) / count)),
                            axis: axis
                        ).Normalize();

                        branches.Add(item: new SdfRigidFrame(
                            Mirrored: false,
                            Position: FixedVector3.Zero,
                            Rotation: rotation
                        ));

                        if (polar.Mirror) {
                            branches.Add(item: new SdfRigidFrame(
                                Mirrored: true,
                                Position: FixedVector3.Zero,
                                Rotation: (rotation * mirrorRemainder).Normalize()
                            ));
                        }
                    }

                    return true;
                }
            case SdfDomainOp.Wallpaper: {
                    refusal = "a wallpaper domain op, which has no rigid-copy expansion";

                    return false;
                }
            default:
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(op),
                    actualValue: op,
                    message: "The domain op kind is not defined."
                );
        }
    }

    /// <summary>Returns whether <paramref name="domain"/> expands to a finite copy set within
    /// <see cref="DefaultCopyBudget"/>, and that set.</summary>
    /// <param name="domain">The ordered ops, or null/empty for the identity copy.</param>
    /// <param name="frames">The copies; a single identity frame when there are no ops, and empty on refusal.</param>
    /// <param name="refusal">Empty on success; otherwise a noun phrase naming what could not expand.</param>
    /// <returns><see langword="true"/> when the chain expanded.</returns>
    public static bool TryExpand(IReadOnlyList<SdfDomainOp>? domain, out SdfRigidFrame[] frames, out string refusal) =>
        TryExpand(
            copyBudget: DefaultCopyBudget,
            domain: domain,
            frames: out frames,
            refusal: out refusal
        );
    /// <summary>Returns whether <paramref name="domain"/> expands to a finite copy set within
    /// <paramref name="copyBudget"/>, and that set.</summary>
    /// <param name="domain">The ordered ops, or null/empty for the identity copy.</param>
    /// <param name="copyBudget">The largest copy count to produce.</param>
    /// <param name="frames">The copies; a single identity frame when there are no ops, and empty on refusal.</param>
    /// <param name="refusal">Empty on success; otherwise a noun phrase naming what could not expand.</param>
    /// <returns><see langword="true"/> when the chain expanded.</returns>
    /// <remarks>A chain past <paramref name="copyBudget"/> is refused from its branch counts alone, so refusing costs
    /// O(1) time and memory however many copies the authored values name — the contract an authored, and so possibly
    /// hostile, domain list is read under.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="copyBudget"/> is not positive.</exception>
    public static bool TryExpand(IReadOnlyList<SdfDomainOp>? domain, int copyBudget, out SdfRigidFrame[] frames, out string refusal) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: copyBudget);

        frames = [];
        refusal = string.Empty;

        if (domain is not { Count: > 0 } ops) {
            frames = [SdfRigidFrame.Identity];

            return true;
        }

        var accumulated = new List<SdfRigidFrame> { SdfRigidFrame.Identity };
        var branches = new List<SdfRigidFrame>();
        var composed = new List<SdfRigidFrame>();

        foreach (var op in ops) {
            if (!TryBranches(
                accumulated: accumulated.Count,
                branches: branches,
                copyBudget: copyBudget,
                op: op,
                refusal: out refusal
            )) {
                frames = [];

                return false;
            }

            composed.Clear();

            foreach (var outer in accumulated) {
                foreach (var inner in branches) {
                    composed.Add(item: outer.Compose(inner: inner));
                }
            }

            accumulated.Clear();
            accumulated.AddRange(collection: composed);
        }

        frames = [.. accumulated];

        return true;
    }
}
