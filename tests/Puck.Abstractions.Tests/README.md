# Puck.Abstractions.Tests

This suite exercises the neutral contracts and value carriers in
`Puck.Abstractions`. It covers abstraction shape, frame-capture requests,
machine content carriers, and self-registration through
`AbstractionsContractTests`, `FrameCaptureRequestTests`, and
`MachineContentCarrierTests`.

The work-counting laws need no GPU. `WorkCountingLawTests` covers work kinds,
monotonic counts, and the difference between an undeclared kind and a zero
count. `GpuWorkLedgerLawTests` and `GpuWorkCountingLawTests` drive the GPU work
ledger and its pass-through wrappers over `FakeGpuDevice`, the shared stand-in
in `tests/Shared`, which holds fences until the test signals them and, for the
wrapper laws, counts each call. `GpuStandingPassLawTests` distinguishes a
retained pass from an inactive pass in text and JSON, and keeps both from
inventing executed zero counts.

`GpuWorkDetailLawTests` combines plain and named rows through modeled kernel
readback, including a 64-bit count and detail growth across pending frames.
It holds grow-only identities and slot capacity, text and JSON detail output,
and the absence of counts on skipped details.

`GpuResidencyLawTests` covers residency without a device. It fills memory
profiles from fixture Vulkan types and heaps and Direct3D 12 values, pins one
policy for each of four synthetic devices, and reads one `GpuRegion`'s bytes
back under all three policies through `UploadModelGpu`, the shared memory model
in `tests/Shared` that runs the copy kernel.

`CacheRetentionLawTests` holds the per-user caches' one retention policy: least
recently used out under a count and a byte bound, the entry in use kept and
counted first, an age bound, an enforcement that leaves a file a reader holds,
and a writer that lists a directory at most once an interval.
`BuildOutputLinkLawTests` reads this suite's own built output: each project
reference is a hard link to the referenced project's `bin`, and no file shares
its file with a compiler output under `obj`, which the compiler rewrites in
place. It reads file identities through the Windows file API and skips
elsewhere. It carries the `BuildTree` trait: `puck gate` runs it in the checkout
it built, and CI's test job, which restores an archive of `bin` outputs rather
than building, leaves it out.

## Running

```powershell
dotnet test tests/Puck.Abstractions.Tests/Puck.Abstractions.Tests.csproj -c Release
```

## Documentation

📚 [Puck.Abstractions](../../src/Puck.Abstractions/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
