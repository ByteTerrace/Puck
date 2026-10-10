#ifndef SDF_INDIRECT_RECEIVER_CERTIFICATE_HLSLI
#define SDF_INDIRECT_RECEIVER_CERTIFICATE_HLSLI
// This pixel owns a completed certificate independently of the shared acceleration hash's occupancy. New visibility storage
// clears it, a surface point its launch ball no longer reaches withdraws it, and any transport or slot replacement changes
// its exact allocation/revision scope. Deferred work is never stored as a completed unresolved answer.
bool sdfIndirectReceiverRecordValid(uint record) {
    uint count, stride;
    sdfVisibilityRecordBuffer.GetDimensions(count, stride);
    return record <= count && SdfVisibilityWords <= count - record;
}
bool sdfIndirectReceiverCertificate(uint record, out uint level, out uint mask, out float3 launched, out float clearance) {
    level = 0u;
    mask = 0u;
    launched = 0.0;
    clearance = 0.0;
    if (!sdfIndirectReceiverRecordValid(record)) { return false; }
    uint word = record + SdfVisibilityRowI;
    uint terminal = sdfVisibilityRecordBuffer[word + 7u];
    level = (terminal >> 8u) & 3u;
    mask = terminal & 255u;
    launched = 0.0;
    clearance = 0.0;
    if ((terminal & 0x80000000u) == 0u ||
        any(uint2(sdfVisibilityRecordBuffer[word + 4u], sdfVisibilityRecordBuffer[word + 5u]) != passGroup.indirectAllocation) ||
        sdfVisibilityRecordBuffer[word + 6u] != passGroup.indirectCertificateRevision) { return false; }
    launched = asfloat(uint3(sdfVisibilityRecordBuffer[word], sdfVisibilityRecordBuffer[word + 1u], sdfVisibilityRecordBuffer[word + 2u]));
    clearance = asfloat(sdfVisibilityRecordBuffer[word + 3u]);
    return level < sdfIndirectLevelCount() && all(isfinite(launched)) && isfinite(clearance);
}
// Whether a retained certificate still belongs to the pixel's current surface point: its certified launch ball, widened
// by the threshold primary accepted the surface within, reaches the point. A new camera sample moves the point within a
// pixel and keeps the certificate; a new surface (another object, a disocclusion) leaves the ball and proves afresh. An
// unresolved certificate stores the surface point itself with no clearance.
bool sdfIndirectCertificateJoins(float3 surfacePoint, float3 launched, float clearance, float threshold) {
    float gap = length(surfacePoint - launched);
    return isfinite(gap) && isfinite(threshold) && gap <= max(clearance, 0.0) + max(threshold, 0.0);
}
#ifdef SDF_RECEIVER_PASS
// Withdraws a retained certificate its pixel's surface left, before the receiver proves the new one.
void sdfIndirectWithdrawReceiverCertificate(uint record) {
    if (sdfIndirectReceiverRecordValid(record)) { sdfVisibilityStoreRow(record + SdfVisibilityRowI + 4u, uint4(0u, 0u, 0u, 0u)); }
}
void sdfIndirectStoreReceiverCertificate(uint record, uint level, uint mask, float3 launched, float clearance, bool completed) {
    if (!completed || !sdfIndirectReceiverRecordValid(record)) { return; }
    uint word = record + SdfVisibilityRowI;
    sdfVisibilityStoreRow(word, uint4(asuint(launched), asuint(clearance)));
    sdfVisibilityStoreRow(word + 4u, uint4(passGroup.indirectAllocation, passGroup.indirectCertificateRevision,
        0x80000000u | (level << 8u) | mask));
}
#endif

#endif
