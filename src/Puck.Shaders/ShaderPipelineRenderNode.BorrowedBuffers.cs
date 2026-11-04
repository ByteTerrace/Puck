namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    // Resolve before any port, cadence decision or readback records. Ownership follows the exact allocation, rather
    // than a recorder which can follow another owner while earlier submission slots still use the old allocation.
    private void RefreshBorrowedBuffers() {
        foreach (var resource in m_resources) {
            if (resource.BorrowedFactory is not { } factory) { continue; }
            var previous = resource.Buffers![0];
            var acquired = factory.BorrowedBuffer(context: resource.BorrowedContext!, built: null,
                resource: resource.Spec, previous: previous)
                ?? throw new InvalidDataException(message: $"Borrowed buffer '{resource.Spec.Name}' lost its allocation.");
            if (ReferenceEquals(acquired.Buffer, previous)) {
                // A stable acquisition needs no new hold. Tolerate an eager provider without retaining a duplicate.
                acquired.Ownership?.Dispose();
                continue;
            }
            if (acquired.Buffer.SizeBytes < resource.BorrowedRequiredBytes) {
                acquired.Ownership?.Dispose();
                throw new InvalidDataException(message: $"Borrowed buffer '{resource.Spec.Name}' holds {acquired.Buffer.SizeBytes} bytes, requires {resource.BorrowedRequiredBytes}.");
            }

            var ownership = resource.BorrowedOwnership;
            Array.Fill(array: resource.Buffers, value: acquired.Buffer);
            resource.BorrowedOwnership = acquired.Ownership;
            Array.Clear(array: resource.Initialized);
            m_initializationPending = true;
            foreach (var version in resource.Cadence) { version.Valid = false; }
            if (ownership is not null) {
                // The same retirement door handles submitted commands and named-publication consumers. Borrowed
                // ownership has zero node-owned bytes; the allocation remains charged to its package owner.
                var published = IsPublishedBuffer(handle: previous.BufferHandle);
                m_retired.Add(item: new RetiredGraph(passes: [], resources: [], preview: null, allocation: ownership,
                    bytes: 0, fence: (published ? null : m_lastSubmissionFence), afterSubmission: m_submissions,
                    publishedBorrowedBuffers: (published ? [previous] : [])));
            }
        }
    }
}
