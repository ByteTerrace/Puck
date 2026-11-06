namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // The packed stream's field sections in vectors, from the counts that size them: the one statement of the field's
    // layout. The constructor packs at these offsets from a program's exact counts, and SdfProgramBuilder.MeasureCapacity
    // bounds PartCompilationWordCapacity from upper counts it reads off the builder, so a capacity probe never packs. No
    // appearance count sizes a field section: the appearance section follows the part programs (SdfAppearance.VectorsOf).
    internal readonly record struct Layout(
        int Instructions,
        int Segments,
        int Instances,
        int WorldSegments,
        int RigidLeaves,
        int ConvexPolygonVectors,
        int SweepCurves,
        int PathEdges
    ) {
        public int DataOffset => (ProgramHeaderVectors + Instructions);
        public int BoundsOffset => (DataOffset + (InstructionDataVectors * Instructions));
        public int SegmentOffset => (BoundsOffset + (BoundRecordVectors * Instructions));
        public int InstanceOffset => ((SegmentOffset + DirectoryHeaderVectors) + (BoundRecordVectors * Segments));
        public int WorldSegmentOffset => ((InstanceOffset + DirectoryHeaderVectors) + (BoundRecordVectors * Instances));
        public int RigidPlanOffset => ((WorldSegmentOffset + DirectoryHeaderVectors) + WorldSegments);
        public int ConvexPolygonOffset => ((RigidPlanOffset + Segments) + (3 * RigidLeaves));
        public int SweepOffset => (ConvexPolygonOffset + ConvexPolygonVectors);
        public int PathOffset => (SweepOffset + (SweepCurveVectorsPerEntry * SweepCurves));
        public int PartProgramOffset => checked((PathOffset + (2 * PathEdges)));

        // A probe cannot rely on shared or ineligible parts staying that way in every later emission. Each leaf needs
        // at least ResetPoint + ShapeBlend, so its shared record plus placement binding consume at most Instructions
        // vectors in total, even if no two placements share geometry. The appearance section follows the part programs.
        public int PartCompilationWordCapacity(int reservedPathWords, int appearanceVectors) => checked((((PartProgramOffset + ((Instances == 0)
            ? 0
            : ((1 + Instances) + Instructions)) + appearanceVectors) * WordsPerVector) + reservedPathWords));
    }
}
