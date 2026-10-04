using System.Text;
using Puck.Assets;
using Puck.Maths;

namespace Puck.World;

/// <summary>The drawn definition's exact curvature splines, loaded before admission asks for their derivation.</summary>
public sealed class CurvatureChunk : ICompiledWorldChunk {
    private CurvatureChunk() { }

    /// <summary>Gets the standard curvature derivation.</summary>
    public static CurvatureChunk Instance { get; } = new();
    /// <inheritdoc />
    public ChunkCode Code { get; } = ChunkCode.Parse(text: "CURV");
    /// <inheritdoc />
    public IReadOnlyList<ChunkCode> DependsOn => [DefinitionChunk.Instance.Code];
    /// <inheritdoc />
    public bool DerivesOnBoot => true;
    /// <inheritdoc />
    public uint Version => 1;

    /// <inheritdoc />
    public AssetContentHash? ReadInput(CompiledWorldContext context, string name) => null;
    /// <inheritdoc />
    public bool TryDerive(CompiledWorldContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CompiledWorldProduct? product, out string reason) {
        product = null;
        try {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            var curves = context.RequireDrawn().Curves;
            var errors = new List<string>();

            _ = WorldDefinitionValidator.ValidateCurves(curves: curves, errors: errors);
            if (errors.Count > 0) {
                reason = string.Join(separator: "; ", values: errors);
                return false;
            }
            writer.Write(value: curves.Count);
            foreach (var curve in curves) { curve.Compiled.Write(writer: writer); }
            writer.Flush();
            product = new CompiledWorldProduct(stream.ToArray(), []);
            reason = string.Empty;
            return true;
        } catch (CurvatureSplineException exception) {
            reason = exception.Message;
            return false;
        }
    }
    /// <inheritdoc />
    public bool TryLoad(CompiledWorldContext context, ReadOnlyMemory<byte> payload, out string reason) {
        try {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            var curves = context.RequireDrawn().Curves;

            if (reader.ReadInt32() != curves.Count) { throw new InvalidDataException(message: "The curvature row count differs."); }
            var splines = new CompiledCurvatureSpline[curves.Count];

            for (var index = 0; (index < splines.Length); index++) {
                splines[index] = CompiledCurvatureSpline.Read(reader: reader);
                if ((splines[index].Closed != curves[index].Closed) || (splines[index].SegmentCount != (curves[index].Knots.Count - (curves[index].Closed ? 0 : 1)))) {
                    throw new InvalidDataException(message: "The curvature knot count or closure differs.");
                }
            }
            if (stream.Position != stream.Length) { throw new InvalidDataException(message: "Trailing curvature chunk bytes."); }
            for (var index = 0; (index < splines.Length); index++) { curves[index].RestoreCompiled(spline: splines[index]); }
            reason = string.Empty;
            return true;
        } catch (Exception exception) when ((exception is IOException or FormatException or ArgumentException or OverflowException)) {
            reason = exception.Message;
            return false;
        }
    }
}
