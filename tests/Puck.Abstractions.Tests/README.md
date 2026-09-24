# Puck.Abstractions.Tests

This suite exercises the neutral contracts and value carriers in
`Puck.Abstractions`. It covers abstraction shape, frame-capture requests,
machine content carriers, and self-registration through
`AbstractionsContractTests`, `FrameCaptureRequestTests`, and
`MachineContentCarrierTests`.

The work-counting laws need no GPU. `WorkCountingLawTests` covers work kinds,
monotonic counts, and the difference between an undeclared kind and a zero
count. `GpuWorkLedgerLawTests` and `GpuWorkCountingLawTests` drive the GPU work
ledger and its pass-through wrappers over `FakeGpu`, a recording stand-in with
fences the test signals by hand.

`GpuResidencyLawTests` covers residency without a device. It fills memory
profiles from fixture Vulkan types and heaps and Direct3D 12 values, pins one
policy for each of four synthetic devices, and reads one `GpuRegion`'s bytes
back under all three policies through `UploadModelGpu`, the shared memory model
in `tests/Shared` that runs the copy kernel.

## Running

```powershell
dotnet test tests/Puck.Abstractions.Tests/Puck.Abstractions.Tests.csproj -c Release
```

## Documentation

📚 [Puck.Abstractions](../../src/Puck.Abstractions/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
