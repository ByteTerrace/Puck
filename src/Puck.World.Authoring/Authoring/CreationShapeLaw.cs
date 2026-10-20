using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.World.Authoring;

/// <summary>How faithfully a shape's lowered field measures distance to its surface.</summary>
public enum CreationShapeDistance {
    /// <summary>The field is the distance: a gain of one and no step clamp.</summary>
    Exact,
    /// <summary>The field underestimates distance by at most its <see cref="CreationShapeLaw.Gain"/>: a gauge or a
    /// non-uniform scale, whose gradient magnitude lies between the gain and one.</summary>
    Bound,
    /// <summary>The field certifies no distance gain: a warp, a field op, a fold, a path or a sweep.</summary>
    Uncertified,
}

/// <summary>
/// What one authored shape of a creation is once lowered: the instructions the renderer emits for it alone, whether that
/// field is the distance, a bound, or uncertified (<see cref="SdfProgram.InstanceDistanceGain"/>, the program's step
/// scale and its scope clamps), whether the fixed-point evaluator interprets it, and what deterministic contact makes of
/// it on a solid placement. The read-back behind <c>creation.shape</c> and <c>puck creation shape</c>.
/// </summary>
/// <param name="Index">The shape's index in its creation's shape list.</param>
/// <param name="Shape">The authored shape, in the engine frame.</param>
/// <param name="Lowered">The render lowering of the shape alone, its domain folds aside, as instruction names.</param>
/// <param name="Distance">The lowering's distance class.</param>
/// <param name="Gain">The certified distance gain in [0, 1]: the field is at least this times the distance.</param>
/// <param name="StepScale">The smallest step scale the lowering's warps or blends bake (1/L), one for an isometric
/// field.</param>
/// <param name="CpuRefusal">Why <see cref="SdfFieldEvaluator"/> refuses the render lowering read as the renderer draws
/// it (<see cref="SdfFieldReading.Render"/>, what the mesh audit measures a bake against), or <see langword="null"/>
/// when it interprets it.</param>
/// <param name="Contact">What deterministic contact makes of the shape on a solid placement: <c>admitted</c>,
/// <c>omitted (…)</c> or <c>refused (…)</c>.</param>
/// <param name="ContactOmits">The instructions the render lowering carries that the contact lowering drops: the
/// render-only facets a body never touches.</param>
/// <param name="Failure">Why the shape could not be lowered at all, or <see langword="null"/>.</param>
public sealed record CreationShapeLaw(
    int Index,
    ShapeDocument Shape,
    string Lowered,
    CreationShapeDistance Distance,
    float Gain,
    float StepScale,
    string? CpuRefusal,
    string Contact,
    IReadOnlyList<string> ContactOmits,
    string? Failure
) {
    /// <summary>Describes the law on one line: the shape's name, its authored type and facets, its lowering, its
    /// distance class, and its CPU and contact verdicts.</summary>
    /// <returns>The line, without a trailing newline.</returns>
    public string Describe() {
        var line = new StringBuilder();
        var shape = Shape;

        _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $"{Index} {(shape.Name?.Value ?? "-")} {shape.Type}");
        if (shape.Profile is { } profile) {
            _ = line.Append(value: ((profile.Kind == SdfPrismProfileKind.Polygon)
                ? $"/{profile.Kind}{profile.Sides.ToString(provider: CultureInfo.InvariantCulture)}"
                : $"/{profile.Kind}"));
        }
        if (shape.Lift is { } lift) { _ = line.Append(value: $"/{lift}"); }
        _ = line.Append(value: $" scale={Vector(value: shape.Scale)}");
        if (shape.Type == SdfSolidPrimitive.Superellipsoid) { _ = line.Append(value: $" e={Number(value: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent))}"); }
        if (Facets(shape: shape) is { Length: > 0 } facets) { _ = line.Append(value: $" facets={facets}"); }
        if (Failure is { } failure) {
            return line.Append(value: $" lowering=refused ({failure})").ToString();
        }
        _ = line.Append(value: $" lowered={Lowered}");
        _ = line.Append(value: Distance switch {
            CreationShapeDistance.Exact => " distance=exact",
            CreationShapeDistance.Bound => $" distance=bound gain={Number(value: Gain)} (|grad d| in [{Number(value: Gain)}, 1])",
            _ => " distance=uncertified (no certified gain)",
        });
        if (StepScale < 1f) { _ = line.Append(value: $" step={Number(value: StepScale)} (L={Number(value: (1f / StepScale))})"); }
        _ = line.Append(value: ((CpuRefusal is null) ? " cpu=interprets" : $" cpu=refuses ({CpuRefusal})"));
        _ = line.Append(value: $" contact={Contact}");
        if (ContactOmits.Count > 0) { _ = line.Append(value: $" contact-omits={string.Join(separator: '+', values: ContactOmits)}"); }
        return line.ToString();
    }

    // The authored facets that change the lowering beyond the primitive, by their document spelling.
    private static string Facets(ShapeDocument shape) {
        List<string> facets = [];

        if (shape.Domain is { Count: > 0 } domain) { facets.Add(item: $"domain:{string.Join(separator: '+', values: domain.Select(selector: static op => op.GetType().Name))}"); }
        if (shape.Rounding is > 0f) { facets.Add(item: "rounding"); }
        if (shape.Chamfer is > 0f) { facets.Add(item: "chamfer"); }
        if (shape.Dilate is > 0f) { facets.Add(item: "dilate"); }
        if (shape.Onion is > 0f) { facets.Add(item: "onion"); }
        if (shape.Flare is not null) { facets.Add(item: "flare"); }
        if (shape.Shear is not null) { facets.Add(item: "shear"); }
        if (shape.Bumps is { Count: > 0 }) { facets.Add(item: "bumps"); }
        if (shape.Erode is not null) { facets.Add(item: "erode"); }
        if (shape.Cells is not null) { facets.Add(item: "cells"); }
        if (shape.Panel is not null) { facets.Add(item: "panel"); }
        if (shape.Trims is { Count: > 0 }) { facets.Add(item: "trims"); }
        if (shape.Detail == true) { facets.Add(item: "detail"); }
        if ((shape.Blend ?? SdfBlendOp.Union) != SdfBlendOp.Union) { facets.Add(item: $"blend:{shape.Blend}"); }
        return string.Join(separator: ',', values: facets);
    }
    internal static string Number(float value) => value.ToString(format: "0.###", provider: CultureInfo.InvariantCulture);
    internal static string Vector(Vector3 value) => (((value.X == value.Y) && (value.Y == value.Z))
        ? Number(value: value.X)
        : $"({Number(value: value.X)},{Number(value: value.Y)},{Number(value: value.Z)})");
}

/// <summary>Derives a <see cref="CreationShapeLaw"/> for each authored shape from its own lowering: the renderer's
/// (<see cref="CreationStampEmitter.EmitShapeStamp"/>) and the contact compiler's (<see cref="CreationStampEmitter.EmitFixed"/>),
/// each built alone into a program of one instance and read back through the program's own analyses, never a table kept
/// beside them.</summary>
public static partial class CreationShapeLaws {
    // A gauge's certified gain sits one evaluation margin under one when the gauge is a sphere (SdfProgram's
    // GaugeEvaluationMargin); at or above it the field is the distance to within that margin.
    private const float ExactGain = (1f - (1f / 1024f));

    private static readonly CreationStampTransform Identity = new(
        Origin: Vector3.Zero,
        Rotation: Quaternion.Identity,
        Scale: 1f,
        ReflectionNormal: null
    );
    private static readonly FixedCreationStampTransform FixedIdentity = new(
        Origin: FixedVector3.Zero,
        Rotation: FixedQuaternion.Identity,
        Scale: FixedQ4816.One,
        ReflectionNormal: null
    );

    /// <summary>The refusals a solid placement owes a shape before any contact compiler sees it, as the clause that follows
    /// "whose shape N": a revolved lift on every solid row, and a profile no deterministic field interprets where field
    /// contact is required. A detail shape owes none, since it reaches no contact compiler. Domain folds that cannot expand
    /// are refused through <see cref="ShapeDomainOps.TryExpand"/>.</summary>
    /// <param name="shape">The authored shape.</param>
    /// <param name="requiresField">Whether the world requires deterministic field contact.</param>
    /// <returns>The refusal clauses, in the order the validator reports them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    public static IEnumerable<string> SolidRefusals(ShapeDocument shape, bool requiresField) {
        ArgumentNullException.ThrowIfNull(shape);

        if (shape.Detail == true) { yield break; }
        if (requiresField && (shape.Profile?.Kind == SdfPrismProfileKind.Path)) {
            yield return $"uses profile {shape.Profile!.Kind}; this profile has no deterministic field-contact evaluator. Use an analytic contact provider or a supported contact shape.";
        }
        // Both contact compilers read a per-axis-scaled local box off SdfSolidGeometry.GetLocalBounds. A revolve reads
        // scale.z as a radial offset rather than an extent, so that box would not describe the solid a body stands on.
        if (shape.Lift is SdfLift.Revolve) {
            yield return "lifts by revolve; a revolve reads scale.z as a radial offset, which no contact compiler's per-axis local box describes. Use the extrude lift on a solid row.";
        }
    }
    /// <summary>Describes a creation's shape laws: a summary line naming the creation and counting each verdict, then one
    /// line a shape (<see cref="CreationShapeLaw.Describe"/>), indented.</summary>
    /// <param name="verb">The verb the summary line opens with, as <c>[verb: …]</c>.</param>
    /// <param name="prototype">The prototype id the creation is authored under.</param>
    /// <param name="laws">The laws to describe, in the creation's order.</param>
    /// <param name="shapeCount">The creation's whole shape count.</param>
    /// <returns>The lines, joined by line feeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="laws"/> is <see langword="null"/>.</exception>
    public static string Describe(string verb, string prototype, IReadOnlyList<CreationShapeLaw> laws, int shapeCount) {
        ArgumentNullException.ThrowIfNull(laws);

        var described = laws.Where(predicate: static law => (law.Failure is null)).ToList();
        var builder = new StringBuilder();

        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[{verb}: {prototype} shapes={laws.Count}/{shapeCount}");
        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" exact={described.Count(predicate: static law => (law.Distance == CreationShapeDistance.Exact))}");
        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" bound={described.Count(predicate: static law => (law.Distance == CreationShapeDistance.Bound))}");
        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" uncertified={described.Count(predicate: static law => (law.Distance == CreationShapeDistance.Uncertified))}");
        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" cpu-refused={laws.Count(predicate: static law => (law.CpuRefusal is not null))}");
        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" contact-refused={laws.Count(predicate: static law => law.Contact.StartsWith(value: "refused", comparisonType: StringComparison.Ordinal))}");
        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" unlowered={(laws.Count - described.Count)}]");
        foreach (var law in laws) {
            _ = builder.Append(value: "\n  ").Append(value: law.Describe());
        }
        return builder.ToString();
    }
    /// <summary>Reads every shape of a creation.</summary>
    /// <param name="document">The creation, in the engine frame (<see cref="CreationFrame.ToEngine"/>).</param>
    /// <returns>One law a shape, in the creation's order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<CreationShapeLaw> Of(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var shapes = (document.Shapes ?? []);
        var laws = new CreationShapeLaw[shapes.Count];

        for (var index = 0; (index < laws.Length); index++) {
            laws[index] = Of(document: document, index: index);
        }
        return laws;
    }
    /// <summary>Reads one shape of a creation.</summary>
    /// <param name="document">The creation, in the engine frame.</param>
    /// <param name="index">The shape's index in <see cref="CreationDocument.Shapes"/>.</param>
    /// <returns>The shape's law; a shape the renderer cannot lower reads its refusal as <see cref="CreationShapeLaw.Failure"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> names no shape.</exception>
    public static CreationShapeLaw Of(CreationDocument document, int index) {
        ArgumentNullException.ThrowIfNull(document);

        var shapes = (document.Shapes ?? []);

        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: shapes.Count);

        var authored = shapes[index];
        // The shape's own field: its domain folds repeat the field (a fold's exactness is the copy's while the prototype
        // fits its cell), and a union keeps its blend from answering an empty accumulator. The posed shape's program is
        // the one whose gain and step clamps the frame bakes, since a warp's bound reads the pose before it; the lowering
        // names the shape at its own origin, where its rigid pose emits nothing.
        var posed = (authored with { Blend = SdfBlendOp.Union, Domain = null, Smooth = null });
        var own = (posed with { Position = Vector3.Zero, Rotation = Quaternion.Identity });

        try {
            return Read(
                authored: authored,
                index: index,
                own: own,
                posed: Render(document: Replace(document: document, index: index, shape: posed), index: index),
                unposed: Render(document: Replace(document: document, index: index, shape: own), index: index)
            );
        } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) {
            return new CreationShapeLaw(
                Contact: "unknown",
                ContactOmits: [],
                CpuRefusal: null,
                Distance: CreationShapeDistance.Uncertified,
                Failure: Sentence(exception: exception),
                Gain: 0f,
                Index: index,
                Lowered: string.Empty,
                Shape: authored,
                StepScale: 1f
            );
        }
    }

    // The law of a shape from its posed program and its program at its own origin (own).
    private static CreationShapeLaw Read(ShapeDocument authored, ShapeDocument own, int index, SdfProgram posed, SdfProgram unposed) {
        // A plane's instance is unbounded, so the gain analysis certifies nothing about it; its field is the distance.
        var lowered = Lowering(program: unposed);
        var gain = (string.Equals(a: lowered, b: nameof(SdfShapeType.Plane), comparisonType: StringComparison.Ordinal) ? 1f : posed.InstanceDistanceGain(index: 0));
        var stepScale = posed.FieldScopeClamps.Aggregate(seed: posed.StepScale, func: static (smallest, clamp) => MathF.Min(x: smallest, y: clamp.StepScale));
        var distance = (((gain >= ExactGain) && (stepScale >= 1f))
            ? CreationShapeDistance.Exact
            : ((gain > 0f) ? CreationShapeDistance.Bound : CreationShapeDistance.Uncertified));
        var (contact, omits) = Contact(authored: authored, own: own, render: unposed);

        return new CreationShapeLaw(
            Contact: contact,
            ContactOmits: omits,
            CpuRefusal: CpuRefusal(program: posed, reading: SdfFieldReading.Render),
            Distance: distance,
            Failure: null,
            Gain: ((distance == CreationShapeDistance.Exact) ? 1f : gain),
            Index: index,
            Lowered: lowered,
            Shape: authored,
            StepScale: stepScale
        );
    }
    private static CreationDocument Replace(CreationDocument document, int index, ShapeDocument shape) =>
        (document with { Shapes = [.. document.Shapes!.Select(selector: (candidate, at) => ((at == index) ? shape : candidate))] });

    // The renderer's emission of one shape under the identity stamp, as one instance bounded by its stamp bound.
    private static SdfProgram Render(CreationDocument document, int index) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var (center, radius) = CreationStampEmitter.ShapeStampBound(document: document, shapeIndex: index, transform: Identity);

        _ = builder.BeginInstance(boundCenter: center, boundRadius: radius);
        CreationStampEmitter.EmitShapeStamp(builder: builder, document: document, material: material, paletteIds: [material], shapeIndex: index, transform: Identity);
        _ = builder.EndInstance();
        return builder.Build();
    }
    // The contact verdict on a solid placement: omitted for a detail shape or a sweep, which no contact compiler emits;
    // refused for a solid-row refusal, a fold that cannot expand, or a lowering the fixed-point field cannot interpret;
    // admitted otherwise, with the render instructions the contact lowering drops.
    private static (string Contact, IReadOnlyList<string> Omits) Contact(ShapeDocument authored, ShapeDocument own, SdfProgram render) {
        if (authored.Detail == true) { return ("omitted (detail)", []); }
        if (authored.Type == SdfSolidPrimitive.Sweep) { return ("omitted (a sweep is not a closed solid)", []); }

        var refusals = SolidRefusals(requiresField: true, shape: authored).ToList();

        if (!ShapeDomainOps.TryExpand(domain: authored.Domain, frames: out _, refusal: out var fold, subject: authored.Position)) {
            refusals.Add(item: $"carries {fold}");
        }
        if (refusals.Count > 0) {
            return ($"refused ({string.Join(separator: "; ", values: refusals.Select(selector: static refusal => refusal.Split(separator: ';')[0]))})", []);
        }

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.EmitFixed(builder: builder, document: new CreationDocument(Schema: null, Name: null, Palette: null, Shapes: [own], Frames: null), materialFor: _ => material, transform: FixedIdentity);

        var contact = builder.Build();

        if (CpuRefusal(program: contact) is { } refusal) {
            return ($"refused ({refusal})", []);
        }

        var dropped = Names(program: render).ToList();

        foreach (var name in Names(program: contact)) {
            _ = dropped.Remove(item: name);
        }
        return ("admitted", dropped);
    }
    // Why the fixed-point evaluator refuses a program, in its own words up to the first sentence, or null. The render
    // lowering is read as the renderer draws it (the reading the mesh audit measures against); the contact lowering as
    // clearance.
    private static string? CpuRefusal(SdfProgram program, SdfFieldReading reading = SdfFieldReading.Contact) {
        try {
            _ = new SdfFieldEvaluator(program: program, reading: reading);
            return null;
        } catch (ArgumentException exception) {
            return Sentence(exception: exception);
        }
    }
    // An exception's first sentence, without its parameter suffix or the instruction index of the one-shape program it
    // was built in, which names nothing in the creation.
    private static string Sentence(Exception exception) {
        var message = exception.Message;
        var end = message.IndexOf(value: ". ", comparisonType: StringComparison.Ordinal);
        var sentence = ((end >= 0) ? message[..end] : message.TrimEnd('.'));
        var parameter = sentence.IndexOf(value: " (Parameter", comparisonType: StringComparison.Ordinal);

        return InstructionIndex().Replace(input: ((parameter >= 0) ? sentence[..parameter] : sentence), replacement: string.Empty).ReplaceLineEndings(replacementText: " ");
    }
    [GeneratedRegex(pattern: @"instruction \d+'s ")]
    private static partial Regex InstructionIndex();
    // The program's instructions, each by name, its stamp prefix's identities and resets left out.
    private static IEnumerable<string> Names(SdfProgram program) {
        foreach (var instruction in program.Instructions) {
            if (Name(instruction: instruction) is { } name) { yield return name; }
        }
    }
    private static string? Name(SdfInstruction instruction) => instruction.Op switch {
        SdfOp.ResetPoint => null,
        SdfOp.Translate => null,
        SdfOp.Rotate => ((new Quaternion(x: instruction.Data0.X, y: instruction.Data0.Y, z: instruction.Data0.Z, w: instruction.Data0.W) == Quaternion.Identity) ? null : "Rotate"),
        SdfOp.Scale => (((instruction.Data0.X == 1f) && (instruction.Data0.Y == 1f) && (instruction.Data0.Z == 1f))
            ? null
            : $"Scale({CreationShapeLaw.Vector(value: new Vector3(x: instruction.Data0.X, y: instruction.Data0.Y, z: instruction.Data0.Z)).Trim('(', ')')})"),
        SdfOp.ShapeBlend => Shape(instruction: instruction),
        _ => instruction.Op.ToString(),
    };
    private static string Shape(SdfInstruction instruction) {
        var shape = (SdfShapeType)instruction.Shape;
        var detail = (instruction.Detail ? "(detail)" : string.Empty);

        return shape switch {
            SdfShapeType.Superellipsoid => $"Superellipsoid(e={CreationShapeLaw.Number(value: instruction.Data0.W)}){detail}",
            _ => $"{shape}{detail}",
        };
    }
    private static string Lowering(SdfProgram program) => string.Join(separator: '·', values: Names(program: program));
}
