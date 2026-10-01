using Puck.SignedDistance;

namespace Puck.World.Tests.FidgetStudy;

/// <summary>Which side of a compose survives over a region, in Fidget's terms: the accumulator alone, the candidate
/// alone, or both.</summary>
public enum SdfStudyChoice : byte {
    /// <summary>The instruction is not a compose.</summary>
    None = 0,
    /// <summary>The accumulator alone survives; the candidate is dead.</summary>
    Left = 1,
    /// <summary>The candidate alone survives; the earlier accumulator is dead.</summary>
    Right = 2,
    /// <summary>Both sides can matter.</summary>
    Both = 3,
}
/// <summary>Why the composes of one or more interval walks survived or died, tallied after liveness.</summary>
public sealed class SdfStudyAttribution {
    /// <summary>The chain-feature flags a shape compose is tallied under.</summary>
    public static readonly string[] FlagNames = ["rotate", "scale", "dynamic", "symmetry", "repeat", "repeat-polar", "wallpaper", "elongate", "unsupported", "in-scope", "relief-in-acc"];

    /// <summary>Gets the shape composes walked.</summary>
    public long Shapes { get; set; }
    /// <summary>Gets the shape composes found dead.</summary>
    public long DeadShapes { get; set; }
    /// <summary>Gets the live smooth composes a hard compose would have decided (the blend radius kept them).</summary>
    public long SmoothBlocked { get; set; }
    /// <summary>Gets the live composes whose candidate had no finite bound (an unmodelled op on their chain).</summary>
    public long UnboundedLive { get; set; }

    /// <summary>Gets the shape composes per blend, then those found dead.</summary>
    public long[,] ByBlend { get; } = new long[32, 2];
    /// <summary>Gets the shape composes per chain flag, then those found dead.</summary>
    public long[,] ByFlag { get; } = new long[FlagNames.Length, 2];

    /// <summary>Clears every count.</summary>
    public void Clear() {
        Shapes = 0;
        DeadShapes = 0;
        SmoothBlocked = 0;
        UnboundedLive = 0;
        Array.Clear(array: ByBlend);
        Array.Clear(array: ByFlag);
    }
    /// <summary>Adds another tally.</summary>
    /// <param name="other">The other tally.</param>
    public void Add(SdfStudyAttribution other) {
        Shapes += other.Shapes;
        DeadShapes += other.DeadShapes;
        SmoothBlocked += other.SmoothBlocked;
        UnboundedLive += other.UnboundedLive;
        for (var row = 0; (row < 32); row++) {
            ByBlend[row, 0] += other.ByBlend[row, 0];
            ByBlend[row, 1] += other.ByBlend[row, 1];
        }
        for (var row = 0; (row < FlagNames.Length); row++) {
            ByFlag[row, 0] += other.ByFlag[row, 0];
            ByFlag[row, 1] += other.ByFlag[row, 1];
        }
    }
}
/// <summary>A conservative interval evaluator over a <see cref="SdfStudyTape"/>, with min/max choice tracking.
/// <para>The region is a ball. Each transform maps the ball to a ball containing its image (isometries exactly, a
/// scale by its smallest factor, a fold by the union of its reachable cells), and each primitive, which the kernels
/// keep 1-Lipschitz, encloses its range over the ball in the centered (Lipschitz) form <c>f(c) ± r</c> times the
/// chain's distance scale. Composes combine intervals and record which side survives: a hard min or max is decided
/// when the intervals separate, a smooth one only when they separate by more than its radius. An op with no model
/// leaves its chain's candidates unbounded. Endpoints are carried as float32 values rounded outward, with every
/// double operation that produces one rounded outward first; <see cref="Outward"/> turns that off for the law that
/// proves it matters.</para></summary>
public sealed class SdfStudyInterval {
    private const int FlagDynamic = (1 << 2);
    private const int FlagElongate = (1 << 7);
    private const int FlagInScope = (1 << 9);
    private const int FlagPolar = (1 << 5);
    private const int FlagRelief = (1 << 10);
    private const int FlagRepeat = (1 << 4);
    private const int FlagRotate = (1 << 0);
    private const int FlagScale = (1 << 1);
    private const int FlagSymmetry = (1 << 3);
    private const int FlagUnsupported = (1 << 8);
    private const int FlagWallpaper = (1 << 6);

    private readonly SdfStudyTape m_tape;
    private readonly int[] m_order;
    private readonly SdfStudyChoice[] m_choice;
    private readonly int[] m_flags;
    private readonly bool[] m_smoothBlocked;
    private readonly bool[] m_unbounded;
    private readonly double[] m_rotationNorm;

    /// <summary>Initializes a new instance of the <see cref="SdfStudyInterval"/> class.</summary>
    /// <param name="tape">The tape to walk.</param>
    /// <param name="outward">Whether endpoints round outward.</param>
    public SdfStudyInterval(SdfStudyTape tape, bool outward = true) {
        m_tape = tape;
        Outward = outward;
        m_order = new int[tape.InstructionCount];
        m_choice = new SdfStudyChoice[tape.InstructionCount];
        m_flags = new int[tape.InstructionCount];
        m_smoothBlocked = new bool[tape.InstructionCount];
        m_unbounded = new bool[tape.InstructionCount];
        m_rotationNorm = new double[tape.InstructionCount];
        for (var index = 0; (index < tape.InstructionCount); index++) {
            if (tape.Instructions[index].Op == SdfOp.Rotate) {
                m_rotationNorm[index] = SdfStudyShapes.RotationNormBound(q: tape.Instructions[index].Data0);
            }
        }
    }

    /// <summary>Gets a value indicating whether endpoints round outward.</summary>
    public bool Outward { get; }

    /// <summary>Encloses the field over a ball and, when asked, marks the instructions live there.</summary>
    /// <param name="segments">The segments to walk, ascending.</param>
    /// <param name="center">The ball's world center.</param>
    /// <param name="radius">The ball's radius.</param>
    /// <param name="live">The live set this walk ORs its live instructions into, or <see langword="null"/>.</param>
    /// <param name="attribution">The tally this walk adds its compose outcomes to, or <see langword="null"/>.</param>
    /// <returns>The enclosure of the field after the program's step scale.</returns>
    public (double Lo, double Hi) Walk(ReadOnlySpan<int> segments, V3 center, double radius, bool[]? live, SdfStudyAttribution? attribution) {
        const double Far = SdfStudyShapes.FarDistance;
        var tape = m_tape;

        var (accLo, accHi) = (Far, Far);
        var (savedLo, savedHi) = (Far, Far);
        var c = center;
        var r = radius;
        var scale = 1.0;
        var flags = 0;
        var relief = false;
        var savedRelief = false;
        var inScope = false;
        var count = 0;

        foreach (var segmentIndex in segments) {
            var segment = tape.Segments[segmentIndex];

            for (var index = segment.First; (index < segment.End); index++) {
                var ins = tape.Instructions[index];

                m_order[count] = index;
                m_choice[count] = SdfStudyChoice.None;
                switch (ins.Op) {
                    case SdfOp.ResetPoint:
                        c = center;
                        r = radius;
                        scale = 1.0;
                        flags = 0;
                        break;
                    case SdfOp.Translate:
                        c -= new V3(X: ins.Data0.X, Y: ins.Data0.Y, Z: ins.Data0.Z);
                        r = Pad(center: c, radius: r);
                        break;
                    case SdfOp.Rotate:
                        c = SdfStudyShapes.RotateInverse(p: c, q: ins.Data0);
                        r = Pad(radius: Up(x: (r * m_rotationNorm[index])), center: c);
                        flags |= FlagRotate;
                        break;
                    case SdfOp.Scale:
                        c = new V3(X: (c.X / ins.Data0.X), Y: (c.Y / ins.Data0.Y), Z: (c.Z / ins.Data0.Z));
                        r = Pad(radius: Up(x: (r / Math.Min(val1: Math.Abs(value: ins.Data0.X), val2: Math.Min(val1: Math.Abs(value: ins.Data0.Y), val2: Math.Abs(value: ins.Data0.Z))))), center: c);
                        scale *= ins.Data0.W;
                        flags |= FlagScale;
                        break;
                    case SdfOp.TransformDynamic: {
                            var transform = tape.Transforms[((int)ins.Data0.X)];
                            var q = (((double)transform.Orientation.X), ((double)transform.Orientation.Y), ((double)transform.Orientation.Z), ((double)transform.Orientation.W));

                            c = SdfStudyShapes.RotateInverse(p: (c - new V3(X: transform.Position.X, Y: transform.Position.Y, Z: transform.Position.Z)), q: q);
                            r = Pad(radius: Up(x: (r * SdfStudyShapes.RotationNormBound(q: q))), center: c);
                            flags |= FlagDynamic;
                            break;
                        }
                    case SdfOp.SymmetryPlane: {
                            var normal = new V3(X: ins.Data0.X, Y: ins.Data0.Y, Z: ins.Data0.Z);
                            var side = (V3.Dot(a: c, b: normal) + ins.Data0.W);
                            var stretch = (1.0 + (4.0 * Math.Abs(value: (V3.Dot(a: normal, b: normal) - 1.0))));

                            if (side >= Up(x: (r * stretch))) {
                                r = Pad(center: c, radius: r);
                            } else if (side <= -Up(x: (r * stretch))) {
                                c -= (normal * (2.0 * Math.Min(val1: side, val2: 0.0)));
                                r = Pad(radius: Up(x: (r * stretch)), center: c);
                            } else {
                                c -= (normal * side);
                                r = Pad(radius: Up(x: (Up(x: (r + Math.Abs(value: side))) * stretch)), center: c);
                            }
                            flags |= FlagSymmetry;
                            break;
                        }
                    case SdfOp.Repeat:
                    case SdfOp.RepeatLimited:
                        (c, r) = RepeatBall(c: c, ins: ins, r: r);
                        r = Pad(center: c, radius: r);
                        flags |= FlagRepeat;
                        break;
                    case SdfOp.RepeatPolar:
                        (c, r) = PolarBall(c: c, ins: ins, r: r);
                        r = Pad(center: c, radius: r);
                        flags |= FlagPolar;
                        break;
                    case SdfOp.Elongate:
                        c -= new V3(
                            X: Math.Clamp(value: c.X, min: -ins.Data0.X, max: ins.Data0.X),
                            Y: Math.Clamp(value: c.Y, min: -ins.Data0.Y, max: ins.Data0.Y),
                            Z: Math.Clamp(value: c.Z, min: -ins.Data0.Z, max: ins.Data0.Z)
                        );
                        r = Pad(center: c, radius: r);
                        flags |= FlagElongate;
                        break;
                    case SdfOp.WallpaperFold:
                        flags |= FlagWallpaper;
                        break;
                    case SdfOp.Onion:
                        if (accLo >= 0.0) {
                            (accLo, accHi) = (FloorF(x: Down(x: (accLo - ins.Data0.X))), CeilF(x: Up(x: (accHi - ins.Data0.X))));
                        } else if (accHi <= 0.0) {
                            (accLo, accHi) = (FloorF(x: Down(x: (-accHi - ins.Data0.X))), CeilF(x: Up(x: (-accLo - ins.Data0.X))));
                        } else {
                            (accLo, accHi) = (FloorF(x: Down(x: -ins.Data0.X)), CeilF(x: Up(x: (Math.Max(val1: -accLo, val2: accHi) - ins.Data0.X))));
                        }
                        break;
                    case SdfOp.Dilate:
                        (accLo, accHi) = (FloorF(x: Down(x: (accLo - ins.Data0.X))), CeilF(x: Up(x: (accHi - ins.Data0.X))));
                        break;
                    case SdfOp.Displace:
                        (accLo, accHi) = Widen(lo: accLo, hi: accHi, reach: Math.Abs(value: ins.Data0.W));
                        relief = true;
                        break;
                    case SdfOp.NoiseDisplace: {
                            var octaveBound = 0.0;
                            var amplitude = 1.0;

                            for (var octave = 0u; (octave < ins.Blend); octave++) {
                                octaveBound += amplitude;
                                amplitude *= Math.Abs(value: ins.Data0.Z);
                            }
                            (accLo, accHi) = Widen(lo: accLo, hi: accHi, reach: Up(x: (Math.Abs(value: (ins.Data0.Y * ins.Data1.X)) * Up(x: (octaveBound * (1.0 + 1e-9))))));
                            relief = true;
                            break;
                        }
                    case SdfOp.CellDisplace: {
                            // The cellular distance lies in [0, 2 sqrt 3]; the relief is amplitude * (distance - 0.5).
                            var a = (ins.Data0.Y * -0.5);
                            var b = (ins.Data0.Y * ((2.0 * Math.Sqrt(d: 3.0)) - 0.5));

                            (accLo, accHi) = (FloorF(x: Down(x: (accLo + Down(x: Math.Min(val1: a, val2: b))))), CeilF(x: Up(x: (accHi + Up(x: Math.Max(val1: a, val2: b))))));
                            relief = true;
                            break;
                        }
                    case SdfOp.PushField:
                        (savedLo, savedHi) = (accLo, accHi);
                        (accLo, accHi) = (Far, Far);
                        savedRelief = relief;
                        relief = false;
                        inScope = true;
                        break;
                    case SdfOp.PopField: {
                            var (candLo, candHi) = (accLo, accHi);

                            if (ins.Data1.Y > 0.0) {
                                (candLo, candHi) = (FloorF(x: Down(x: (candLo * ins.Data1.Y))), CeilF(x: Up(x: (candHi * ins.Data1.Y))));
                            }
                            relief = (savedRelief || relief);
                            inScope = false;
                            (m_choice[count], accLo, accHi, m_smoothBlocked[count]) = Compose(blend: ((SdfBlendOp)ins.Blend), smooth: ins.Data1.X, aLo: savedLo, aHi: savedHi, bLo: candLo, bHi: candHi);
                            m_flags[count] = 0;
                            m_unbounded[count] = false;
                            break;
                        }
                    case SdfOp.ShapeBlend: {
                            var shape = ((SdfShapeType)ins.Shape);
                            double candLo;
                            double candHi;
                            var unsupported = (((flags & (FlagWallpaper | FlagUnsupported)) != 0) || !SdfStudyShapes.Supports(shape: shape));

                            if (unsupported) {
                                (candLo, candHi) = (double.NegativeInfinity, double.PositiveInfinity);
                            } else {
                                var value = SdfStudyShapes.Evaluate(shape: shape, p: c, d0: ins.Data0, d1: ins.Data1);

                                candLo = FloorF(x: Down(x: (Down(x: (value - r)) * scale)));
                                // A glyph's lettering lies inside its quad, so the quad bounds it from below only.
                                candHi = ((shape == SdfShapeType.Glyph) ? double.PositiveInfinity : CeilF(x: Up(x: (Up(x: (value + r)) * scale))));
                            }
                            (m_choice[count], accLo, accHi, m_smoothBlocked[count]) = Compose(blend: ((SdfBlendOp)ins.Blend), smooth: ins.Data1.X, aLo: accLo, aHi: accHi, bLo: candLo, bHi: candHi);
                            m_flags[count] = flags | (inScope ? FlagInScope : 0) | (relief ? FlagRelief : 0) | (unsupported ? FlagUnsupported : 0);
                            m_unbounded[count] = unsupported;
                            break;
                        }
                    default:
                        flags |= FlagUnsupported;
                        break;
                }
                count++;
            }
        }
        if ((live is not null) || (attribution is not null)) {
            MarkLive(attribution: attribution, count: count, live: live);
        }

        return (FloorF(x: Down(x: (accLo * tape.StepScale))), CeilF(x: Up(x: (accHi * tape.StepScale))));
    }
    /// <summary>Returns the rewrites a pruned tape needs: a live intersection- or subtraction-like compose whose
    /// accumulator had earlier composes, none of them live, becomes a copy.</summary>
    /// <param name="segments">The segments the pruned tape walks.</param>
    /// <param name="live">The pruned tape's live set.</param>
    /// <returns>The per-instruction rewrites.</returns>
    public SdfStudyRewrite[] Rewrites(ReadOnlySpan<int> segments, bool[] live) {
        var rewrites = new SdfStudyRewrite[m_tape.InstructionCount];

        var (any, anyLive) = (false, false);
        var (savedAny, savedAnyLive) = (false, false);

        foreach (var segmentIndex in segments) {
            var segment = m_tape.Segments[segmentIndex];

            for (var index = segment.First; (index < segment.End); index++) {
                var ins = m_tape.Instructions[index];

                switch (ins.Op) {
                    case SdfOp.PushField:
                        (savedAny, savedAnyLive) = (any, anyLive);
                        (any, anyLive) = (false, false);
                        break;
                    case SdfOp.PopField:
                        (any, anyLive) = (savedAny, savedAnyLive);
                        Visit(index: index, blend: ((SdfBlendOp)ins.Blend));
                        break;
                    case SdfOp.ShapeBlend:
                        Visit(index: index, blend: ((SdfBlendOp)ins.Blend));
                        break;
                }
            }
        }

        return rewrites;

        void Visit(int index, SdfBlendOp blend) {
            if (live[index]) {
                if (any && !anyLive) {
                    rewrites[index] = blend switch {
                        SdfBlendOp.Intersection or SdfBlendOp.SmoothIntersection => SdfStudyRewrite.Copy,
                        SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction => SdfStudyRewrite.CopyNegated,
                        _ => SdfStudyRewrite.None,
                    };
                }
                anyLive = true;
            }
            any = true;
        }
    }

    private void MarkLive(int count, bool[]? live, SdfStudyAttribution? attribution) {
        var accLive = true;
        var chainNeeded = false;
        Span<(bool Saved, bool Scope)> scopes = stackalloc (bool, bool)[4];
        var depth = 0;

        for (var k = (count - 1); (k >= 0); k--) {
            var index = m_order[k];
            var ins = m_tape.Instructions[index];
            bool isLive;

            switch (ins.Op) {
                case SdfOp.ShapeBlend: {
                        var choice = m_choice[k];

                        isLive = (accLive && (choice != SdfStudyChoice.Left));
                        accLive = (accLive && (choice != SdfStudyChoice.Right));
                        chainNeeded |= isLive;
                        if (attribution is not null) {
                            Attribute(attribution: attribution, k: k, blend: ins.Blend, isLive: isLive);
                        }
                        break;
                    }
                case SdfOp.PopField: {
                        var choice = m_choice[k];

                        isLive = (accLive && (choice != SdfStudyChoice.Left));
                        scopes[depth++] = ((accLive && (choice != SdfStudyChoice.Right)), isLive);
                        accLive = isLive;
                        break;
                    }
                case SdfOp.PushField: {
                        var (saved, scope) = scopes[--depth];

                        isLive = scope;
                        accLive = saved;
                        break;
                    }
                case SdfOp.Onion:
                case SdfOp.Dilate:
                    isLive = accLive;
                    break;
                case SdfOp.Displace:
                case SdfOp.NoiseDisplace:
                case SdfOp.CellDisplace:
                    isLive = accLive;
                    chainNeeded |= isLive;
                    break;
                case SdfOp.ResetPoint:
                    isLive = chainNeeded;
                    chainNeeded = false;
                    break;
                default:
                    isLive = chainNeeded;
                    break;
            }
            if (isLive && (live is not null)) {
                live[index] = true;
            }
        }
    }
    private void Attribute(SdfStudyAttribution attribution, int k, uint blend, bool isLive) {
        attribution.Shapes++;
        attribution.ByBlend[((int)Math.Min(val1: blend, val2: 31u)), 0]++;
        if (!isLive) {
            attribution.DeadShapes++;
            attribution.ByBlend[((int)Math.Min(val1: blend, val2: 31u)), 1]++;
        } else {
            if (m_smoothBlocked[k]) {
                attribution.SmoothBlocked++;
            }
            if (m_unbounded[k]) {
                attribution.UnboundedLive++;
            }
        }
        for (var bit = 0; (bit < SdfStudyAttribution.FlagNames.Length); bit++) {
            if ((m_flags[k] & (1 << bit)) != 0) {
                attribution.ByFlag[bit, 0]++;
                if (!isLive) {
                    attribution.ByFlag[bit, 1]++;
                }
            }
        }
    }
    private (SdfStudyChoice Choice, double Lo, double Hi, bool SmoothBlocked) Compose(SdfBlendOp blend, double smooth, double aLo, double aHi, double bLo, double bHi) {
        switch (blend) {
            case SdfBlendOp.Union:
                return MinChoice(aHi: aHi, aLo: aLo, bHi: bHi, bLo: bLo, k: 0.0);
            case SdfBlendOp.SmoothUnion:
                return MinChoice(k: SdfStudyShapes.SmoothRadius(smooth: smooth), aLo: aLo, aHi: aHi, bLo: bLo, bHi: bHi);
            case SdfBlendOp.Intersection:
                return MaxChoice(aHi: aHi, aLo: aLo, bHi: bHi, bLo: bLo, k: 0.0);
            case SdfBlendOp.SmoothIntersection:
                return MaxChoice(k: SdfStudyShapes.SmoothRadius(smooth: smooth), aLo: aLo, aHi: aHi, bLo: bLo, bHi: bHi);
            case SdfBlendOp.Subtraction:
                return MaxChoice(aHi: aHi, aLo: aLo, bHi: -bLo, bLo: -bHi, k: 0.0);
            case SdfBlendOp.SmoothSubtraction:
                return MaxChoice(k: SdfStudyShapes.SmoothRadius(smooth: smooth), aLo: aLo, aHi: aHi, bLo: -bHi, bHi: -bLo);
            case SdfBlendOp.GrooveUnion: {
                    var chamfer = Math.Max(val1: smooth, val2: 0.0);

                    return (SdfStudyChoice.Both, Math.Min(val1: aLo, val2: bLo), CeilF(x: Math.Max(val1: Math.Min(val1: aHi, val2: bHi), val2: Up(x: (chamfer - LengthLo(aHi: aHi, aLo: aLo, bHi: bHi, bLo: bLo))))), false);
                }
            case SdfBlendOp.PipeUnion: {
                    var chamfer = Math.Max(val1: smooth, val2: 0.0);

                    return (SdfStudyChoice.Both, FloorF(x: Math.Min(val1: Math.Min(val1: aLo, val2: bLo), val2: Down(x: (LengthLo(aHi: aHi, aLo: aLo, bHi: bHi, bLo: bLo) - chamfer)))), Math.Min(val1: aHi, val2: bHi), false);
                }
            case SdfBlendOp.ChamferUnion: {
                    var chamfer = Math.Max(val1: smooth, val2: 0.0);

                    return (SdfStudyChoice.Both,
                        FloorF(x: Math.Min(val1: Math.Min(val1: aLo, val2: bLo), val2: Down(x: (Down(x: (Down(x: (aLo + bLo)) - chamfer)) * 0.7071067811865476)))),
                        CeilF(x: Math.Min(val1: Math.Min(val1: aHi, val2: bHi), val2: Up(x: (Up(x: (Up(x: (aHi + bHi)) - chamfer)) * 0.7071067811865477)))),
                        false);
                }
            default:
                return (SdfStudyChoice.Both, double.NegativeInfinity, double.PositiveInfinity, false);
        }
    }
    // min(a, b), or the polynomial smooth minimum of radius k, which lies in [min - k/4, min] and equals the min
    // exactly once the operands separate by k.
    private (SdfStudyChoice, double, double, bool) MinChoice(double k, double aLo, double aHi, double bLo, double bHi) {
        if (bLo >= Up(x: (aHi + k))) {
            return (SdfStudyChoice.Left, aLo, aHi, false);
        }
        if (aLo > Up(x: (bHi + k))) {
            return (SdfStudyChoice.Right, bLo, bHi, false);
        }

        var blocked = ((k > 0.0) && ((bLo >= aHi) || (aLo > bHi)));

        return (SdfStudyChoice.Both, FloorF(x: Down(x: (Math.Min(val1: aLo, val2: bLo) - (0.25 * k)))), Math.Min(val1: aHi, val2: bHi), blocked);
    }
    private (SdfStudyChoice, double, double, bool) MaxChoice(double k, double aLo, double aHi, double bLo, double bHi) {
        if (aLo >= Up(x: (bHi + k))) {
            return (SdfStudyChoice.Left, aLo, aHi, false);
        }
        if (bLo > Up(x: (aHi + k))) {
            return (SdfStudyChoice.Right, bLo, bHi, false);
        }

        var blocked = ((k > 0.0) && ((aLo >= bHi) || (bLo > aHi)));

        return (SdfStudyChoice.Both, Math.Max(val1: aLo, val2: bLo), CeilF(x: Up(x: (Math.Max(val1: aHi, val2: bHi) + (0.25 * k)))), blocked);
    }
    private (V3, double) RepeatBall(V3 c, double r, SdfStudyTape.Instruction ins) {
        var limited = (ins.Op == SdfOp.RepeatLimited);
        var center = c;
        var crossingCenter = c;
        var spread = 0.0;
        var cellSpread = 0.0;

        for (var axis = 0; (axis < 3); axis++) {
            var spacing = Component(ins.Data0, axis);
            var value = c.At(axis: axis);
            var kCenter = Cell(axis: axis, value: value);
            var kLo = Cell(value: Down(x: (value - r)), axis: axis);
            var kHi = Cell(value: Up(x: (value + r)), axis: axis);

            if (kLo > kHi) {
                (kLo, kHi) = (kHi, kLo);
            }
            if (kLo == kHi) {
                center = center.With(axis: axis, value: (value - (spacing * kCenter)));
                crossingCenter = crossingCenter.With(axis: axis, value: (value - (spacing * kCenter)));
            } else {
                center = center.With(axis: axis, value: (value - (spacing * (0.5 * (kLo + kHi)))));
                crossingCenter = crossingCenter.With(axis: axis, value: 0.0);
                spread += Math.Pow(x: (Math.Abs(value: spacing) * (0.5 * (kHi - kLo))), y: 2.0);
                cellSpread += Math.Pow(x: (0.5 * Math.Abs(value: spacing)), y: 2.0);
            }
        }
        if (spread == 0.0) {
            return (center, r);
        }

        var lattice = Up(x: (r + Up(x: (Math.Sqrt(d: spread) * (1.0 + 1e-12)))));

        if (!limited) {
            // An unlimited fold lands every crossing axis inside the base cell.
            var cell = Up(x: (Math.Sqrt(d: Up(x: ((r * r) + cellSpread))) * (1.0 + 1e-12)));

            if (cell < lattice) {
                return (crossingCenter, cell);
            }
        }

        return (center, lattice);

        double Cell(double value, int axis) => (limited
            ? Math.Clamp(value: SdfStudyPoint.Round(x: (value / Component(ins.Data0, axis))), min: -Component(ins.Data1, axis), max: Component(ins.Data1, axis))
            : SdfStudyPoint.Round(x: (value * Component(ins.Data1, axis))));
    }
    private static double Component((double X, double Y, double Z, double W) v, int axis) => (axis switch { 0 => v.X, 1 => v.Y, _ => v.Z });
    private (V3, double) PolarBall(V3 c, double r, in SdfStudyTape.Instruction ins) {
        var folded = SdfStudyPoint.PolarFold(ins: ins, p: c);

        var (u, v) = SdfStudyPoint.PolarPlane(axis: ins.Shape);
        var rho = Math.Sqrt(d: ((c.At(axis: u) * c.At(axis: u)) + (c.At(axis: v) * c.At(axis: v))));
        var sectorAngle = ins.Data0.X;

        if (rho > Up(x: (r * 1.000001))) {
            var a = (Math.Atan2(y: c.At(axis: v), x: c.At(axis: u)) + (0.5 * sectorAngle));
            var halfWidth = Up(x: (Math.Asin(d: Math.Min(val1: 1.0, val2: (r / rho))) * 1.000001));
            var divisions = ((ins.Blend != 0u) ? (2.0 / sectorAngle) : (1.0 / sectorAngle));

            if (Math.Floor(d: ((a - halfWidth) * divisions)) == Math.Floor(d: ((a + halfWidth) * divisions))) {
                return (folded, r);
            }
        }

        return (folded, Up(x: (r + Up(x: (Up(x: (rho + r)) * Up(x: (sectorAngle * 1.000001)))))));
    }
    private (double, double) Widen(double lo, double hi, double reach) => (FloorF(x: Down(x: (lo - reach))), CeilF(x: Up(x: (hi + reach))));
    private static double LengthLo(double aLo, double aHi, double bLo, double bHi) {
        var a = (((aLo <= 0.0) && (aHi >= 0.0)) ? 0.0 : Math.Min(val1: Math.Abs(value: aLo), val2: Math.Abs(value: aHi)));
        var b = (((bLo <= 0.0) && (bHi >= 0.0)) ? 0.0 : Math.Min(val1: Math.Abs(value: bLo), val2: Math.Abs(value: bHi)));

        return Math.BitDecrement(x: Math.Sqrt(d: ((a * a) + (b * b))));
    }
    private double Pad(double radius, V3 center) => (Outward
        ? Up(x: (radius + (1e-12 * (((1.0 + Math.Abs(value: center.X)) + Math.Abs(value: center.Y)) + Math.Abs(value: center.Z)))))
        : radius);
    private double Up(double x) => (Outward ? Math.BitIncrement(x: x) : x);
    private double Down(double x) => (Outward ? Math.BitDecrement(x: x) : x);
    private double FloorF(double x) {
        var f = ((float)x);

        if (Outward && (f > x)) {
            f = MathF.BitDecrement(x: f);
        }

        return f;
    }
    private double CeilF(double x) {
        var f = ((float)x);

        if (Outward && (f < x)) {
            f = MathF.BitIncrement(x: f);
        }

        return f;
    }
}
