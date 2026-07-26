// GPU-Driven Occlusion Culling — Occludee Data Definition
// Static object occlusion: GPU frustum + Hi-Z culling, result readback to CPU

using System;
using UnityEngine;

namespace GPUDrivenOcclusion
{
    /// <summary>
    /// Per-occludee data registered by the user.
    /// Holds the renderer reference, world-space bounds, and occluder role used for GPU culling.
    /// </summary>
    [Serializable]
    public struct OccludeeDesc
    {
        /// <summary>World-space axis-aligned bounding box.</summary>
        public Bounds worldBounds;

        /// <summary>Renderer to enable/disable based on visibility result.</summary>
        [NonSerialized]
        public Renderer renderer;

        /// <summary>Whether this object contributes depth to Hi-Z (occluder) or is only culled (occludee).</summary>
        [NonSerialized]
        public bool isOccluder;

        public OccludeeDesc(Renderer renderer, bool isOccluder = true)
        {
            this.renderer = renderer;
            this.worldBounds = renderer.bounds;
            this.isOccluder = isOccluder;
        }

        public void UpdateBounds()
        {
            if (renderer != null)
                worldBounds = renderer.bounds;
        }
    }
}
