// GPU-Driven Occlusion Culling — GPU-Side Data Structures
// Must match C# layout exactly (float3 + float3 + uint, padded to 4-byte alignment)

#ifndef GPUDRIVEN_OCCLUSION_INPUT
#define GPUDRIVEN_OCCLUSION_INPUT

// Per-occludee data uploaded from CPU, read-only by culling CS
struct GPUOccludeeData
{
    float3 boundsCenter;   // world-space AABB center
    float3 boundsExtents;  // world-space AABB half-size
};

// Visibility result per occludee (GPU writes, CPU reads back)
// 0 = hidden, 1 = visible
// Using uint for atomic compatibility (InterlockedOr to set visible)
struct GPUVisibilityResult
{
    uint isVisible;        // 0 = culled, 1 = visible
};

// Packed version for buffer efficiency — one float4 per occludee
struct GPUOccludeePacked
{
    float4 centerAndFlags; // xyz = boundsCenter, w = unused flags
    float4 extentsAndPad;  // xyz = boundsExtents, w = padding
};

#endif // GPUDRIVEN_OCCLUSION_INPUT
