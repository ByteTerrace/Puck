namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // The packed stream's sections in vectors, from the counts that size them: the one statement of the layout. The
    // constructor packs at these offsets from a program's exact counts, and SdfProgramBuilder.MeasureCapacity bounds
    // PartCompilationWordCapacity from upper counts it reads off the builder, so a capacity probe never packs.
    internal readonly record struct Layout(
        int Instructions,
        int Materials,
        int Segments,
        int Instances,
        int WorldSegments,
        int GridWords,
        int RigidLeaves,
        int ChainTableVectors,
        int ConvexPolygonVectors,
        int SweepCurves,
        int PathEdges
    ) {
        public int DataOffset => (ProgramHeaderVectors + Instructions);
        public int MaterialOffset => (DataOffset + (InstructionDataVectors * Instructions));
        public int BoundsOffset => (MaterialOffset + (MaterialVectorsPerEntry * Materials));
        public int SegmentOffset => (BoundsOffset + (BoundRecordVectors * Instructions));
        public int InstanceOffset => ((SegmentOffset + DirectoryHeaderVectors) + (BoundRecordVectors * Segments));
        public int WorldSegmentOffset => ((InstanceOffset + DirectoryHeaderVectors) + (BoundRecordVectors * Instances));
        public int GridOffset => ((WorldSegmentOffset + DirectoryHeaderVectors) + WorldSegments);
        public int RigidPlanOffset => (GridOffset + (GridWords / WordsPerVector));
        public int TapeOffset => ((RigidPlanOffset + Segments) + (3 * RigidLeaves));
        public int ChainTableOffset => (TapeOffset + Instructions);
        public int ConvexPolygonOffset => (ChainTableOffset + ChainTableVectors);
        public int SweepOffset => (ConvexPolygonOffset + ConvexPolygonVectors);
        public int PathOffset => (SweepOffset + (SweepCurveVectorsPerEntry * SweepCurves));
        public int PartProgramOffset => checked((PathOffset + (2 * PathEdges)));

        // A probe cannot rely on shared or ineligible parts staying that way in every later emission. Each leaf needs
        // at least ResetPoint + ShapeBlend, so its shared record plus placement binding consume at most Instructions
        // vectors in total, even if no two placements share geometry.
        public int PartCompilationWordCapacity(int reservedPathWords) => checked((((PartProgramOffset + ((Instances == 0)
            ? 0
            : ((1 + Instances) + Instructions))) * WordsPerVector) + reservedPathWords));
    }

    // The most vectors a chain table over these segments and chains spans: the header, the segment entries and the
    // identity list (ChainTable.Vectors).
    internal static int ChainTableVectorsOf(int segments, int chains) => checked(((DirectoryHeaderVectors + ChainTable.EntryVectors(segments: segments)) + ((chains + (ChainIdentitiesPerVector - 1)) / ChainIdentitiesPerVector)));
}
