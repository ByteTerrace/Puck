#ifndef SDF_INDIRECT_RECEIVER_CERTIFICATE_HLSLI
#define SDF_INDIRECT_RECEIVER_CERTIFICATE_HLSLI
// This pixel owns a completed certificate independently of the shared acceleration hash's occupancy. A primary write
// clears it, and any transport or slot replacement changes its exact allocation/revision scope. Deferred work is never
// stored as a completed unresolved answer.
bool sdfIndirectReceiverCertificate(uint record, out uint level, out uint mask, out float3 launched, out float clearance) {
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
    return true;
}
void sdfIndirectStoreReceiverCertificate(uint record, uint level, uint mask, float3 launched, float clearance, bool completed) {
    if (!completed) { return; }
    uint word = record + SdfVisibilityRowI;
    sdfVisibilityStoreRow(word, uint4(asuint(launched), asuint(clearance)));
    sdfVisibilityStoreRow(word + 4u, uint4(passGroup.indirectAllocation, passGroup.indirectCertificateRevision,
        0x80000000u | (level << 8u) | mask));
}

#endif
