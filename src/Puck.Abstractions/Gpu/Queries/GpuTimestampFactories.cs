namespace Puck.Abstractions.Gpu;

internal sealed class CountingTimestampFactory(IGpuTimestampFactory inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuTimestampFactory {
    public IGpuTimestampPool? Create(uint count, in GpuObjectName name) {
        var pool = inner.Create(count: count, name: in name);

        return ((pool is null) ? null : Created(created: pool, lifetimeIndex: GpuWork.TimestampPoolsCreatedIndex));
    }
}
internal sealed class FaultingTimestampFactory(IGpuTimestampFactory inner, GpuCreationFaults faults) : FaultingWrapper(faults: faults), IGpuTimestampFactory {
    public IGpuTimestampPool? Create(uint count, in GpuObjectName name) {
        Enter(kind: GpuCreationKind.TimestampPool);
        return inner.Create(count: count, name: in name);
    }
}
