// GPU-Driven Occlusion Culling — Occludee Data Definition
// Static object occlusion: GPU frustum + Hi-Z culling, result readback to CPU

using System;
using UnityEngine;

namespace GPUDrivenOcclusion
{
    /// <summary>
    /// Per-occludee data registered by the user.
    /// Holds the renderer reference and world-space bounds used for GPU culling.
    /// </summary>
    [Serializable]
    public struct OccludeeDesc
    {
        /// <summary>World-space axis-aligned bounding box.</summary>
        public Bounds worldBounds;

        /// <summary>Renderer to enable/disable based on visibility result.</summary>
        [NonSerialized]
        public Renderer renderer;

        public OccludeeDesc(Renderer renderer)
        {
            this.renderer = renderer;
            this.worldBounds = renderer.bounds;
        }

        public void UpdateBounds()
        {
            if (renderer != null)
                worldBounds = renderer.bounds;
        }
    }
}
