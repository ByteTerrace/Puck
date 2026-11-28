namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    // Resolve before any port, cadence decision or readback records. Ownership follows the exact allocation, rather
    // than a recorder which can follow another owner while earlier submission slots still use the old allocation.
    // Invariant: a borrowed buffer the node records or publishes is live. The node holds a loan on every allocation it
    // adopts (GpuLentBuffer.Lend, here and at installation), which only its retirement returns, after its submissions and
    // its publication's readers; RenderGraphBorrowedBuffer carries only a GpuLentBuffer, so no package lends without one.
    private void RefreshBorrowedBuffers() {
        foreach (var resource in m_resources) {
            if (resource.BorrowedFactory is not { } factory) { continue; }
            var previous = resource.Buffers![0];
            var acquired = factory.BorrowedBuffer(context: resource.BorrowedContext!, built: null,
                resource: resource.Spec, previous: previous)
                ?? throw new InvalidDataException(message: $"Borrowed buffer '{resource.Spec.Name}' lost its allocation.");
            if (ReferenceEquals(acquired.Buffer, previous)) { continue; }
            if (acquired.Buffer.SizeBytes < resource.BorrowedRequiredBytes) {
                throw new InvalidDataException(message: $"Borrowed buffer '{resource.Spec.Name}' holds {acquired.Buffer.SizeBytes} bytes, requires {resource.BorrowedRequiredBytes}.");
            }

            var ownership = resource.BorrowedOwnership;
            var loan = acquired.Buffer.Lend();
            Array.Fill(array: resource.Buffers, value: acquired.Buffer);
            resource.BorrowedOwnership = loan;
            Array.Clear(array: resource.Initialized);
            m_initializationPending = true;
            foreach (var version in resource.Cadence) { version.Valid = false; }
            if (ownership is not null) {
                // The same retirement door handles submitted commands and named-publication consumers. Borrowed
                // ownership has zero node-owned bytes; the allocation remains charged to its package owner.
                var published = IsPublishedBuffer(handle: previous.BufferHandle);
                var retired = new RetiredGraph(passes: [], resources: [], preview: null, allocation: ownership,
                    bytes: 0, afterSubmission: m_submissions, publishedBorrowedBuffers: (published ? [previous] : []));
                if (published) { m_retiredPublications.Add(retired); }
                else { QueueRetirement(retired); }
            }
        }
    }
}
