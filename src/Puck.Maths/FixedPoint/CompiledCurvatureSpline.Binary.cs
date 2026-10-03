using System.Numerics;

namespace Puck.Maths;

public sealed partial class CompiledCurvatureSpline {
    private const string Magic = "PUCKCUR1";

    /// <summary>Writes the exact compiled coefficients and arc tables, with this codec's shape fingerprint.</summary>
    /// <param name="writer">The destination; it remains open.</param>
    public void Write(BinaryWriter writer) {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(value: Magic);
        writer.Write(value: FormatShapes.CompiledCurvatureSplineMagic);
        writer.Write(value: ((byte)(Closed ? 1 : 0)));
        writer.Write(value: TotalLengthRaw);
        writer.Write(value: m_segments.Length);
        foreach (var segment in m_segments) {
            writer.Write(value: segment.D0X);
            writer.Write(value: segment.D0Z);
            writer.Write(value: segment.D1X);
            writer.Write(value: segment.D1Z);
            writer.Write(value: segment.D2X);
            writer.Write(value: segment.D2Z);
            writer.Write(value: segment.E0X);
            writer.Write(value: segment.E0Z);
            writer.Write(value: segment.E1X);
            writer.Write(value: segment.E1Z);
            writer.Write(value: segment.GradeRaw);
            writer.Write(value: segment.LengthRaw);
            writer.Write(value: segment.P0X);
            writer.Write(value: segment.P0Z);
            writer.Write(value: segment.P1X);
            writer.Write(value: segment.P1Z);
            writer.Write(value: segment.P2X);
            writer.Write(value: segment.P2Z);
            writer.Write(value: segment.P3X);
            writer.Write(value: segment.P3Z);
            writer.Write(value: segment.StationRaw);
            writer.Write(value: segment.Tangent0LengthRaw);
            writer.Write(value: segment.Tangent1LengthRaw);
            writer.Write(value: segment.Y0Raw);
            writer.Write(value: segment.Y1Raw);
            writer.Write(value: segment.ArcTable.Length);
            foreach (var station in segment.ArcTable) { writer.Write(value: station); }
        }
    }
    /// <summary>Reads one compiled spline without solving or narrowing any coefficient.</summary>
    /// <param name="reader">The source; it remains open and is positioned after the spline.</param>
    /// <returns>The exact stored spline.</returns>
    /// <exception cref="InvalidDataException">The format, fingerprint, stations or arc tables are invalid.</exception>
    public static CompiledCurvatureSpline Read(BinaryReader reader) {
        ArgumentNullException.ThrowIfNull(reader);
        if ((reader.ReadString() != Magic) || (reader.ReadString() != FormatShapes.CompiledCurvatureSplineMagic)) {
            throw new InvalidDataException(message: "Foreign compiled curvature spline shape.");
        }
        var closure = reader.ReadByte();
        var length = reader.ReadInt64();
        var count = reader.ReadInt32();

        if ((closure > 1) || (length <= 0) || (count <= 0) || ((closure == 1) && (count < 3))) { throw new InvalidDataException(message: "Invalid compiled curvature spline header."); }
        if (reader.BaseStream.CanSeek && (count > ((reader.BaseStream.Length - reader.BaseStream.Position) / 200))) { throw new InvalidDataException(message: "Truncated compiled curvature spline."); }
        var segments = new CurvatureSplineSegment[count];
        var station = 0L;

        for (var index = 0; (index < count); index++) {
            var segment = new CurvatureSplineSegment {
                D0X = reader.ReadInt64(),
                D0Z = reader.ReadInt64(),
                D1X = reader.ReadInt64(),
                D1Z = reader.ReadInt64(),
                D2X = reader.ReadInt64(),
                D2Z = reader.ReadInt64(),
                E0X = reader.ReadInt64(),
                E0Z = reader.ReadInt64(),
                E1X = reader.ReadInt64(),
                E1Z = reader.ReadInt64(),
                GradeRaw = reader.ReadInt64(),
                LengthRaw = reader.ReadInt64(),
                P0X = reader.ReadInt64(),
                P0Z = reader.ReadInt64(),
                P1X = reader.ReadInt64(),
                P1Z = reader.ReadInt64(),
                P2X = reader.ReadInt64(),
                P2Z = reader.ReadInt64(),
                P3X = reader.ReadInt64(),
                P3Z = reader.ReadInt64(),
                StationRaw = reader.ReadInt64(),
                Tangent0LengthRaw = reader.ReadInt64(),
                Tangent1LengthRaw = reader.ReadInt64(),
                Y0Raw = reader.ReadInt64(),
                Y1Raw = reader.ReadInt64(),
                ArcTable = ReadArcTable(reader: reader),
            };

            if ((segment.StationRaw != station) || (segment.LengthRaw <= 0) || (segment.ArcTable[^1] != segment.LengthRaw) || (segment.LengthRaw > (length - station))) { throw new InvalidDataException(message: "Invalid compiled curvature spline station."); }
            station += segment.LengthRaw;
            segments[index] = segment;
        }
        if (station != length) { throw new InvalidDataException(message: "Invalid compiled curvature spline length."); }
        return new CompiledCurvatureSpline(closed: (closure == 1), segments: segments, totalLengthRaw: length);
    }

    private static long[] ReadArcTable(BinaryReader reader) {
        var count = reader.ReadInt32();

        if ((count < 65) || (count > ((1 << 16) + 1)) || !BitOperations.IsPow2(value: (count - 1))) { throw new InvalidDataException(message: "Invalid compiled curvature spline arc table size."); }
        var table = new long[count];

        for (var index = 0; (index < count); index++) {
            table[index] = reader.ReadInt64();
            if (((index == 0) && (table[index] != 0)) || ((index > 0) && (table[index] <= table[(index - 1)]))) { throw new InvalidDataException(message: "Invalid compiled curvature spline arc table station."); }
        }
        return table;
    }
}
