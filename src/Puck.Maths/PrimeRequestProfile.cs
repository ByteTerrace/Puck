namespace Puck.Maths;

/// <summary>An immutable result and coarse work record from one profiled prime request.</summary>
/// <remarks>Counts are architecture-independent work units, not instruction counts or timings. Narrow uint
/// kernels, individual sieve marks, base-prime generation and survivor decision internals are not counted.
/// Profile entry points call the production implementation and collect work at request, prime-group and
/// segment boundaries. Ordinary entry points allocate no profiler or profile records.</remarks>
public sealed class PrimeRequestProfile {
    /// <summary>Gets the production request's result.</summary>
    public ulong Result { get; }
    /// <summary>Gets OutOfRange, Narrow32, CheckpointSelection, CountedSelection, or CountOnly.</summary>
    public string Route { get; }
    /// <summary>Gets a frozen ordered record of actual ulong count dispatches; narrow-kernel internal calls are excluded.</summary>
    public IReadOnlyList<PrimeCountWork> Counts { get; }
    /// <summary>Gets the number of local selection windows requested.</summary>
    public ulong SelectionWindows { get; }
    /// <summary>Gets the number of local selection bitmap segments marked, including a final partially consumed segment.</summary>
    public ulong SelectionSegments { get; }
    /// <summary>Gets the sum of marked local selection bitmap lengths, in bytes.</summary>
    public ulong SelectionBitmapBytes { get; }
    /// <summary>Gets primality requests in short odd-candidate walks; survivor decisions inside sieved windows are excluded.</summary>
    public ulong DirectPrimalityRequests { get; }

    internal PrimeRequestProfile(ulong result, PrimeRequestWorkBuilder work) {
        Result = result;
        Route = work.Route;
        SelectionWindows = work.SelectionWindows;
        SelectionSegments = work.SelectionBitmap.Segments;
        SelectionBitmapBytes = work.SelectionBitmap.Bytes;
        DirectPrimalityRequests = work.DirectPrimalityRequests;

        var counts = new PrimeCountWork[work.Counts.Count];

        for (var index = 0; (index < counts.Length); ++index) { counts[index] = new(work: work.Counts[index]); }
        Counts = Array.AsReadOnly(array: counts);
    }
}
/// <summary>An immutable coarse work record for one exact ulong prime-count dispatch.</summary>
/// <remarks>Fields unused by the selected route are zero. A frontier is an inclusive integer upper bound;
/// bitmap bytes count logical lengths processed, not allocations or memory traffic. Factor coordinates count
/// entries inspected before filtering, and cached quotient divisions count the explicit x/p cache population.</remarks>
public sealed class PrimeCountWork {
    /// <summary>Gets the inclusive bound submitted to the exact counter.</summary>
    public ulong Bound { get; }
    /// <summary>Gets Narrow32, Checkpoint, CheckpointInterval, Quotient64, or Gourdon.</summary>
    public string Route { get; }
    /// <summary>Gets the active Gourdon cutoff y; this implementation takes z=y.</summary>
    public uint Cutoff { get; }
    /// <summary>Gets the generated factor-table capacity retained by the request.</summary>
    public uint TableCapacity { get; }
    /// <summary>Gets whether this count generated a new factor table.</summary>
    public bool TableBuilt { get; }
    /// <summary>Gets factor coordinates inspected by the ordinary-leaf sum.</summary>
    public ulong OrdinaryFactorCoordinates { get; }
    /// <summary>Gets factor coordinates inspected by the composite hard-leaf sum D.</summary>
    public ulong SpecialFactorCoordinates { get; }
    /// <summary>Gets x/p divisions performed once to populate the active hard-leaf cache.</summary>
    public ulong CachedQuotientDivisions { get; }
    /// <summary>Gets hard-leaf bitmap segments initialized.</summary>
    public ulong LeafSegments { get; }
    /// <summary>Gets initialized hard-leaf bitmap bytes, including word padding.</summary>
    public ulong LeafBitmapBytes { get; }
    /// <summary>Gets the inclusive hard-leaf quotient frontier.</summary>
    public ulong LeafFrontier { get; }
    /// <summary>Gets complete-prime bitmap segments visited by Gourdon's A+C terms.</summary>
    public ulong EasyLeafSegments { get; }
    /// <summary>Gets logical complete-prime bitmap bytes visited by Gourdon's A+C terms.</summary>
    public ulong EasyLeafBitmapBytes { get; }
    /// <summary>Gets the inclusive upper bound requested by the forward semiprime sieve.</summary>
    public ulong SemiprimeForwardFrontier { get; }
    /// <summary>Gets forward semiprime bitmap segments marked.</summary>
    public ulong SemiprimeForwardSegments { get; }
    /// <summary>Gets forward semiprime bitmap bytes marked.</summary>
    public ulong SemiprimeForwardBitmapBytes { get; }
    /// <summary>Gets reverse prime-stream windows generated for semiprime counting.</summary>
    public ulong SemiprimeReverseWindows { get; }
    /// <summary>Gets reverse prime-stream bitmap bytes marked for semiprime counting.</summary>
    public ulong SemiprimeReverseBitmapBytes { get; }
    /// <summary>Gets checkpoint-difference bitmap segments marked.</summary>
    public ulong CheckpointSegments { get; }
    /// <summary>Gets checkpoint-difference bitmap bytes marked.</summary>
    public ulong CheckpointBitmapBytes { get; }

    internal PrimeCountWork(PrimeCountWorkBuilder work) {
        Bound = work.Bound;
        Route = work.Route;
        Cutoff = work.Cutoff;
        TableCapacity = work.TableCapacity;
        TableBuilt = work.TableBuilt;
        OrdinaryFactorCoordinates = work.OrdinaryFactorCoordinates;
        SpecialFactorCoordinates = work.SpecialFactorCoordinates;
        CachedQuotientDivisions = work.CachedQuotientDivisions;
        LeafSegments = work.LeafSegments;
        LeafBitmapBytes = work.LeafBitmapBytes;
        LeafFrontier = work.LeafFrontier;
        EasyLeafSegments = work.EasyLeafSegments;
        EasyLeafBitmapBytes = work.EasyLeafBitmapBytes;
        SemiprimeForwardFrontier = work.SemiprimeForwardFrontier;
        SemiprimeForwardSegments = work.SemiprimeForwardBitmap.Segments;
        SemiprimeForwardBitmapBytes = work.SemiprimeForwardBitmap.Bytes;
        SemiprimeReverseWindows = work.SemiprimeReverseWindows;
        SemiprimeReverseBitmapBytes = work.SemiprimeReverseBitmap.Bytes;
        CheckpointSegments = work.CheckpointBitmap.Segments;
        CheckpointBitmapBytes = work.CheckpointBitmap.Bytes;
    }
}

internal sealed class PrimeRequestWorkBuilder {
    internal string Route { get; set; } = "CountOnly";
    internal List<PrimeCountWorkBuilder> Counts { get; } = [];
    internal PrimeBitmapWork SelectionBitmap { get; } = new();

    internal ulong DirectPrimalityRequests { get; set; }
    internal ulong SelectionWindows { get; set; }

    internal PrimeCountWorkBuilder BeginCount(ulong bound) {
        PrimeCountWorkBuilder work = new(bound: bound);

        Counts.Add(item: work);
        return work;
    }
}
internal sealed class PrimeCountWorkBuilder(ulong bound) {
    internal ulong Bound { get; } = bound;
    internal string Route { get; set; } = "";

    internal ulong CachedQuotientDivisions { get; set; }
    internal uint Cutoff { get; set; }
    internal ulong EasyLeafBitmapBytes { get; set; }
    internal ulong EasyLeafSegments { get; set; }
    internal ulong LeafBitmapBytes { get; set; }
    internal ulong LeafFrontier { get; set; }
    internal ulong LeafSegments { get; set; }
    internal ulong OrdinaryFactorCoordinates { get; set; }
    internal ulong SemiprimeForwardFrontier { get; set; }
    internal ulong SemiprimeReverseWindows { get; set; }
    internal ulong SpecialFactorCoordinates { get; set; }
    internal bool TableBuilt { get; set; }
    internal uint TableCapacity { get; set; }

    internal PrimeBitmapWork SemiprimeForwardBitmap { get; } = new();
    internal PrimeBitmapWork SemiprimeReverseBitmap { get; } = new();
    internal PrimeBitmapWork CheckpointBitmap { get; } = new();
}
internal sealed class PrimeBitmapWork {
    internal ulong Bytes { get; private set; }
    internal ulong Segments { get; private set; }

    internal void AddSegment(int bytes) {
        ++Segments;
        Bytes += ((uint)bytes);
    }
}

public static partial class PrimeExtensions {
    /// <summary>Returns the selected prime and a frozen coarse work record from the production ulong nth-prime implementation.</summary>
    /// <param name="value">The zero-based prime index.</param>
    /// <param name="cancellationToken">Cancels the request with the same contract as ordinary nth-prime selection.</param>
    /// <returns>The result and architecture-independent work counters; see <see cref="PrimeRequestProfile"/> for exclusions.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static PrimeRequestProfile ProfileNthPrime(ulong value, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        PrimeRequestWorkBuilder work = new();
        var result = NthPrime64(cancellationToken: cancellationToken, profile: work, value: value);

        return new(result: result, work: work);
    }
    /// <summary>Returns the prime count and a frozen coarse work record from the production ulong counting implementation.</summary>
    /// <param name="value">The inclusive upper bound.</param>
    /// <param name="cancellationToken">Cancels the request with the same contract as ordinary prime counting.</param>
    /// <returns>The result and architecture-independent work counters; see <see cref="PrimeRequestProfile"/> for exclusions.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static PrimeRequestProfile ProfilePrimeCountingFunction(ulong value, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        PrimeRequestWorkBuilder work = new();
        var result = CountPrimeBound64(cancellationToken: cancellationToken, profile: work, value: value, workspace: null);

        return new(result: result, work: work);
    }
}
