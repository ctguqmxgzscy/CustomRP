// Terrain Grass Data — reads Unity terrain detail prototypes & density maps,
// uploads to GPU buffers for instance generation.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GPUDrivenGrass
{
    /// <summary>
    ///     Per-terrain GPU data bundle: heightmap, density textures, prototype metadata.
    ///     Created once from TerrainData, re-uploaded only when terrain changes.
    /// </summary>
    public class TerrainGrassData : IDisposable
    {
        // -----------------------------------------------------------------
        // Per-prototype descriptor (GPU side via structured buffer)
        // -----------------------------------------------------------------
        [Serializable]
        public struct ProtoDesc
        {
            public int densityWidth;      // detail resolution X
            public int densityHeight;     // detail resolution Z
            public float terrainWidth;    // world size X
            public float terrainHeight;   // world size Z
            public float terrainHeightY;  // world height (terrainData.size.y)
            public float terrainPosX;     // terrain world position X
            public float terrainPosY;     // terrain world position Y
            public float terrainPosZ;     // terrain world position Z
            public float minWidth;        // detail prototype minWidth
            public float maxWidth;        // detail prototype maxWidth
            public float minHeight;       // detail prototype minHeight
            public float maxHeight;       // detail prototype maxHeight
            public float noiseSpread;     // detail prototype noiseSpread
            public int   renderMode;      // 0 = Grass, 1 = VertexLit
            public uint  densityOffset;   // offset into combined density buffer
            public uint  instanceOffset;  // offset into transform buffer for this proto
            public float healthyColorR;   // healthyColor.r
            public float dryColorR;       // dryColor.r
            public int   meshIndex;       // which mesh/material to use for draw
        }

        // -----------------------------------------------------------------
        // Fields
        // -----------------------------------------------------------------
        public ProtoDesc[] PrototypeDescs;
        public Mesh[] ProtoMeshes;            // per-proto mesh from DetailPrototype prefab (null for billboard)
        public Material[] ProtoMaterials;     // per-proto material from prefab MeshRenderer (null if not found)
        public ComputeBuffer DensityBuffer;   // float[totalTexels], packed per-proto density layers
        public ComputeBuffer ProtoDescBuffer; // StructuredBuffer<ProtoDesc>
        public int MaxInstanceCount;          // upper bound: total density texels across all protos

        private readonly List<Texture2D> m_tempTextures = new(); // hold references so GC doesn't kill them

        // -----------------------------------------------------------------
        // Build from scene terrains
        // -----------------------------------------------------------------
        public static TerrainGrassData FromActiveTerrains(float globalDensity = 1f)
        {
            var terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0)
                return null;

            var result = new TerrainGrassData();
            var protoList = new List<ProtoDesc>();
            var densityLists = new List<float[]>(); // per-proto float array
            var perProtoMaxCounts = new List<int>(); // per-proto upper bound instance count

            uint densityOffset = 0;

            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                var td = t.terrainData;
                var prototypes = td.detailPrototypes;
                if (prototypes == null) continue;

                int dw = td.detailWidth;
                int dh = td.detailHeight;
                Vector3 tPos = t.transform.position;
                Vector3 tSize = td.size;

                for (int p = 0; p < prototypes.Length; p++)
                {
                    var dp = prototypes[p];

                    // Read density layer (int[,] 0-16 per cell)
                    var densityInt = td.GetDetailLayer(0, 0, dw, dh, p);
                    int count = dw * dh;
                    var densityFloat = new float[count];
                    int instanceCount = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int x = i % dw;
                        int y = i / dw;
                        float v = densityInt[y, x] * globalDensity * t.detailObjectDensity / 16f;
                        // Quantize: each texel can spawn floor(v) guaranteed + 1 if random < frac(v)
                        int guaranteed = Mathf.FloorToInt(v);
                        float frac = v - guaranteed;
                        // Store: guaranteed count in integer part, fractional prob as float
                        densityFloat[i] = guaranteed + frac;
                        instanceCount += guaranteed; // tight: frac bonus handled by 20% padding below
                    }

                    densityLists.Add(densityFloat);
                    perProtoMaxCounts.Add(instanceCount);

                    protoList.Add(new ProtoDesc
                    {
                        densityWidth   = dw,
                        densityHeight  = dh,
                        terrainWidth   = tSize.x,
                        terrainHeight  = tSize.z,
                        terrainHeightY = tSize.y,
                        terrainPosX    = tPos.x,
                        terrainPosY    = tPos.y,
                        terrainPosZ    = tPos.z,
                        minWidth       = dp.minWidth,
                        maxWidth       = dp.maxWidth,
                        minHeight      = dp.minHeight,
                        maxHeight      = dp.maxHeight,
                        noiseSpread    = dp.noiseSpread,
                        renderMode     = dp.renderMode == DetailRenderMode.VertexLit ? 1 : 0,
                        densityOffset  = densityOffset,
                        instanceOffset = 0, // filled below
                        healthyColorR  = dp.healthyColor.r,
                        dryColorR      = dp.dryColor.r,
                        meshIndex      = p,
                    });

                    densityOffset += (uint)count;
                    result.MaxInstanceCount = Mathf.Max(result.MaxInstanceCount, instanceCount);
                }
            }

            if (protoList.Count == 0) return null;

            // Fill cumulative instance offsets per proto
            uint cumulativeOffset = 0;
            for (int i = 0; i < protoList.Count; i++)
            {
                var desc = protoList[i];
                desc.instanceOffset = cumulativeOffset;
                protoList[i] = desc;
                cumulativeOffset += (uint)(perProtoMaxCounts[i] * 1.2f); // guaranteed + 20% frac padding
            }
            result.MaxInstanceCount = Mathf.Max((int)cumulativeOffset, 1024);

            result.PrototypeDescs = protoList.ToArray();

            // Extract meshes + materials from terrain detail prototype prefabs.
            // Mesh protos (renderMode=1): mesh/mat from prefab → GrassIndirectGeneric shader
            // Billboard protos (renderMode=0): null mesh/mat → fallback quad + blade shader
            int protoCount = result.PrototypeDescs.Length;
            result.ProtoMeshes    = new Mesh[protoCount];
            result.ProtoMaterials = new Material[protoCount];
            int protoIdx = 0;
            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                var prototypes = t.terrainData.detailPrototypes;
                for (int p = 0; p < prototypes.Length && protoIdx < protoCount; p++, protoIdx++)
                {
                    var dp = prototypes[p];
                    if (dp.usePrototypeMesh && dp.prototype != null)
                    {
                        var mf = dp.prototype.GetComponent<MeshFilter>();
                        var mr = dp.prototype.GetComponent<MeshRenderer>();
                        result.ProtoMeshes[protoIdx]    = mf != null ? mf.sharedMesh : null;
                        result.ProtoMaterials[protoIdx] = mr != null ? mr.sharedMaterial : null;
                    }
                    else
                    {
                        result.ProtoMeshes[protoIdx]    = null;
                        result.ProtoMaterials[protoIdx] = null;
                    }
                }
            }

            // Upload combined density buffer
            int totalTexels = (int)densityOffset;
            var combinedDensity = new float[totalTexels];
            int writeOffset = 0;
            foreach (var list in densityLists)
            {
                Array.Copy(list, 0, combinedDensity, writeOffset, list.Length);
                writeOffset += list.Length;
            }
            result.DensityBuffer = new ComputeBuffer(totalTexels, sizeof(float), ComputeBufferType.Structured);
            result.DensityBuffer.SetData(combinedDensity);

            // Upload proto desc buffer
            result.ProtoDescBuffer = new ComputeBuffer(protoList.Count, System.Runtime.InteropServices.Marshal.SizeOf<ProtoDesc>(), ComputeBufferType.Structured);
            result.ProtoDescBuffer.SetData(result.PrototypeDescs);

            return result;
        }

        public void Dispose()
        {
            DensityBuffer?.Release();
            DensityBuffer = null;
            ProtoDescBuffer?.Release();
            ProtoDescBuffer = null;
        }
    }
}
