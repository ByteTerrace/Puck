// Shared rigid point rotations for the field walk and visibility reprojection.
#ifndef FIELD_SDF_QUATERNION_HLSLI
#define FIELD_SDF_QUATERNION_HLSLI
float3 rotatePointByInverseQuaternion(float3 p, float4 q) {
    float3 u = -q.xyz;
    return (p + (2.0 * cross(u, ((q.w * p) + cross(u, p)))));
}
// Forward rotation R(q) p (the inverse's conjugate). The rigid-leaf dual maps a shape-LOCAL gradient to world by the
// leaf's forward rotation, mirroring the inverse rotation the rigid point walk applies (worldGrad = R(q) localGrad).
float3 rotatePointByQuaternion(float3 p, float4 q) {
    float3 u = q.xyz;
    return (p + (2.0 * cross(u, ((q.w * p) + cross(u, p)))));
}

#endif
