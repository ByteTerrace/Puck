using System.Globalization;
using System.Text;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// Generates <see cref="FileName"/>, the kernels' declarations of the SDF instruction set: its version handshake, every
/// enum an instruction word carries, and the packed-layout constants the interpreter decodes words with. Every value is
/// read from the C# model, never transcribed, and an enum member's name is its C# name in upper snake case after the
/// enum's prefix (<see cref="SdfOp.ResetPoint"/> is <c>SDF_OP_RESET_POINT</c>), so a new member reaches the kernels by
/// regenerating. <c>puck shaders generate</c> writes the file beside the kernels and <c>--check</c> fails on drift.
/// <para>The text is a pure function of the model: the same build generates the same bytes, with LF line endings, on
/// every host.</para>
/// </summary>
public static class SdfIsaHlsl {
    /// <summary>The file name of the generated include, which sits beside the kernels in
    /// <c>Assets/Shaders/Sdf</c>.</summary>
    public const string FileName = "sdf-isa.hlsli";

    private const string Guard = "SDF_ISA_HLSLI";

    /// <summary>Generates the include.</summary>
    /// <returns>The HLSL text.</returns>
    /// <exception cref="InvalidOperationException">Two declarations would share one HLSL name.</exception>
    public static string Generate() {
        var declarations = new Declarations();

        declarations.Section(title: "The instruction-set version and the report word the version handshake dispatches with.");
        declarations.Count(
            name: "SDF_ISA_VERSION",
            value: SdfIsa.Version
        );
        declarations.Bits(
            name: "SDF_ISA_REPORT_REQUEST",
            value: SdfShaderSetVerification.ReportRequest
        );
        declarations.Members<SdfOp>(prefix: "SDF_OP");
        declarations.Members<SdfShapeType>(prefix: "SDF_SHAPE");
        declarations.Members<SdfBlendOp>(prefix: "SDF_BLEND");
        declarations.Members<SdfLift>(prefix: "SDF_LIFT");
        declarations.Members<SdfNoiseFlavor>(prefix: "SDF_NOISE");
        declarations.Members<SdfPolarAxis>(prefix: "SDF_POLAR_AXIS");
        declarations.Members<SdfWallpaperGroup>(prefix: "SDF_WPG");
        declarations.Members<SdfWallpaperPlane>(prefix: "SDF_WPG_PLANE");
        declarations.Section(title: "Shape-lane flags and the type mask on a ShapeBlend instruction's header.");
        declarations.Bits(
            name: "SDF_SHAPE_DETAIL_FLAG",
            value: SdfProgram.ShapeDetailFlag
        );
        declarations.Bits(
            name: "SDF_SHAPE_NO_SECONDARY_FLAG",
            value: SdfProgram.ShapeNoSecondaryFlag
        );
        declarations.Bits(
            name: "SDF_SHAPE_TYPE_MASK",
            value: SdfProgram.ShapeTypeMask
        );
        declarations.Section(title: "Bound modes, and the flags and masks packed beside them in bound, segment and instance records.");
        declarations.Count(
            name: "SDF_BOUND_NONE",
            value: SdfProgram.BoundModeNone
        );
        declarations.Count(
            name: "SDF_BOUND_STATIC",
            value: SdfProgram.BoundModeStatic
        );
        declarations.Count(
            name: "SDF_BOUND_DYNAMIC",
            value: SdfProgram.BoundModeDynamic
        );
        declarations.Bits(
            name: "SDF_SEGMENT_RIGID_PLAN",
            value: SdfProgram.SegmentRigidPlanFlag
        );
        declarations.Bits(
            name: "SDF_SEGMENT_BOUND_MASK",
            value: SdfProgram.SegmentBoundModeMask
        );
        declarations.Bits(
            name: "SDF_INSTANCE_SHADOW_TRANSPARENT_BIT",
            value: SdfProgram.ShadowTransparentInstanceFlag
        );
        declarations.Bits(
            name: "SDF_INSTANCE_SEGMENT_END_MASK",
            value: SdfProgram.SegmentEndMask
        );
        declarations.Bits(
            name: "SDF_NO_DETAIL_SHAPES_FLAG",
            value: SdfProgram.NoDetailShapesFlag
        );
        declarations.Section(title: "The rigid-leaf plan.");
        declarations.Bits(
            name: "SDF_RIGID_LEAF_IDENTITY_ROTATION",
            value: SdfProgram.RigidLeafIdentityRotationFlag
        );
        declarations.Bits(
            name: "SDF_RIGID_LEAF_FOLDED",
            value: SdfProgram.RigidLeafFoldedFlag
        );
        declarations.Bits(
            name: "SDF_RIGID_LEAF_SHAPE_MASK",
            value: SdfProgram.RigidLeafShapeMask
        );
        declarations.Count(
            name: "SDF_RIGID_LEAF_MAX_FOLD_RUN",
            value: SdfProgram.RigidLeafMaxFoldRun
        );
        declarations.Section(title: "The sampled-region shape's packed dims.");
        declarations.Bits(
            name: "SDF_SAMPLED_REGION_DIM_MASK",
            value: SdfProgramBuilder.SampledRegionDimMask
        );
        declarations.Section(title: "Program capacities, strides and floors.");
        declarations.Count(
            name: "SDF_MAX_INSTANCES",
            value: SdfProgramBuilder.MaxInstances
        );
        declarations.Count(
            name: "SDF_MATERIAL_VECTORS_PER_ENTRY",
            value: SdfProgram.MaterialVectorsPerEntry
        );
        declarations.Count(
            name: "SDF_GRID_HEADER_WORDS",
            value: SdfInstanceGrid.HeaderWords
        );
        declarations.Count(
            name: "SDF_GRID_MAX_DIM",
            value: SdfInstanceGrid.MaxDimension
        );
        declarations.Count(
            name: "SDF_MAX_FIELD_SCOPE_DEPTH",
            value: SdfProgramBuilder.MaxFieldScopeDepth
        );
        declarations.Real(
            name: "SDF_FLARE_MIN_SCALE",
            value: SdfProgramBuilder.FlareMinScale
        );
        declarations.Real(
            name: "SDF_LANE_ERODE_RAGGED_AMOUNT",
            value: SdfProgramBuilder.LaneErodeRaggedAmount
        );
        declarations.Signed(
            name: "SDF_SCREEN_MATERIAL",
            value: SdfProgramBuilder.ScreenMaterialId
        );

        return declarations.Text();
    }

    // A C# member name's HLSL spelling: upper snake case, with a word break before an upper-case letter that follows a
    // lower-case one or that starts a new word after an upper-case run (P4M stays P4M, LogSphere becomes LOG_SPHERE).
    private static string UpperSnake(string name) {
        var text = new StringBuilder(capacity: (name.Length * 2));

        for (var index = 0; (index < name.Length); index++) {
            var character = name[index];

            if (
                (index > 0) &&
                char.IsUpper(c: character) &&
                (char.IsLower(c: name[(index - 1)]) || (
                    char.IsUpper(c: name[(index - 1)]) &&
                    ((index + 1) < name.Length) &&
                    char.IsLower(c: name[(index + 1)])
                ))
            ) {
                text.Append(value: '_');
            }

            text.Append(value: char.ToUpperInvariant(c: character));
        }

        return text.ToString();
    }

    private sealed class Declarations {
        private readonly List<(string Name, string Value)> m_group = [];
        private readonly HashSet<string> m_names = new(comparer: StringComparer.Ordinal);
        private readonly StringBuilder m_text = new();

        public Declarations() {
            Line(line: "// Generated by `puck shaders generate` from the SDF instruction set's C# model; regenerate it, never edit it.");
            Line(line: $"#ifndef {Guard}");
            Line(line: $"#define {Guard}");
        }

        private void Define(string name, string value) {
            if (!m_names.Add(item: name)) {
                throw new InvalidOperationException(message: $"Two SDF instruction-set declarations are both named {name}.");
            }

            m_group.Add(item: (name, value));
        }
        private void Flush() {
            var width = m_group.Max(selector: static entry => entry.Name.Length);

            foreach (var (name, value) in m_group) {
                m_text.Append(value: $"#define {name.PadRight(totalWidth: width)} {value}").Append(value: '\n');
            }

            m_group.Clear();
        }
        // A group of defines is aligned when the next non-define line ends it.
        private void Line(string line) {
            if (m_group.Count != 0) {
                Flush();
            }

            m_text.Append(value: line).Append(value: '\n');
        }

        public void Bits(string name, uint value) => Define(
            name: name,
            value: $"0x{value.ToString(
                format: "X8",
                provider: CultureInfo.InvariantCulture
            )}u"
        );
        public void Count(string name, long value) {
            ArgumentOutOfRangeException.ThrowIfNegative(value: value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                other: uint.MaxValue,
                value: value
            );
            Define(
                name: name,
                value: $"{value.ToString(provider: CultureInfo.InvariantCulture)}u"
            );
        }
        public void Members<T>(string prefix) where T : struct, Enum {
            Section(title: $"{typeof(T).FullName}.");

            foreach (var member in Enum.GetValues<T>()) {
                Count(
                    name: $"{prefix}_{UpperSnake(name: member.ToString())}",
                    value: Convert.ToInt64(
                        provider: CultureInfo.InvariantCulture,
                        value: member
                    )
                );
            }
        }
        public void Real(string name, float value) {
            if (!float.IsFinite(f: value)) {
                throw new ArgumentOutOfRangeException(
                    actualValue: value,
                    message: "An HLSL literal must be finite.",
                    paramName: nameof(value)
                );
            }

            var spelling = value.ToString(
                format: "R",
                provider: CultureInfo.InvariantCulture
            );

            Define(
                name: name,
                value: ((spelling.AsSpan().IndexOfAny(values: ".E") < 0)
                    ? (spelling + ".0")
                    : spelling)
            );
        }
        public void Section(string title) {
            Line(line: "");
            Line(line: $"// {title}");
        }
        public void Signed(string name, int value) => Define(
            name: name,
            value: value.ToString(provider: CultureInfo.InvariantCulture)
        );
        public string Text() {
            Line(line: "");
            Line(line: $"#endif // {Guard}");

            return m_text.ToString();
        }
    }
}
