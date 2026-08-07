using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
///     体积云 3D 噪声纹理烘焙器（纯编辑器工具，无需场景/组件）。
///     Shape 128³：R = Perlin-Worley 基形（PerlinWorleyGen.compute）
///                 GBA = 三频率 Worley FBM（NoiseGenCompute.compute 逐通道累积）
///     Detail 64³ ：RGB = 三频率 Worley FBM（更高细胞数）
///     产物保存为 Texture3D 资产（ARGB32，Trilinear + Repeat），供 CloudRayMarch.compute 采样。
/// </summary>
public static class CloudNoiseBaker
{
    private const string k_Root = "Assets/Features/VolumetricClouds";
    private const string k_ComputeDir = k_Root + "/Editor/NoiseGenerator";
    private const string k_TexDir = k_Root + "/Textures";
    private const int k_Threads = 8;

    [MenuItem("Tools/Volumetric Clouds/Bake Shape & Detail Noise")]
    public static void BakeAll()
    {
        BakeShape();
        BakeDetail();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Cloud noise textures baked: ShapeNoise3D.asset, DetailNoise3D.asset");
    }

    [MenuItem("Tools/Volumetric Clouds/Bake Shape Noise")]
    public static void BakeShape()
    {
        const int res = 128;
        var rt = CreateVolumeRT(res);
        try
        {
            // R = Perlin-Worley 基形（remap(perlin, 1-worley, 1, 0, 1)，周期性 Perlin 无缝）
            RunPerlinWorley(rt, res, channel: 0, seed: 12345, numCells: 8);
            // GBA = Worley FBM 三频率（细胞数递增，侵蚀细节增多）
            RunWorleyChannel(rt, res, channel: 1, seed: 11, cellsA: 4, cellsB: 8, cellsC: 16, persistence: 0.5f, invert: true);
            RunWorleyChannel(rt, res, channel: 2, seed: 22, cellsA: 8, cellsB: 16, cellsC: 32, persistence: 0.5f, invert: true);
            RunWorleyChannel(rt, res, channel: 3, seed: 33, cellsA: 12, cellsB: 24, cellsC: 48, persistence: 0.5f, invert: true);
            SaveTexture3D(rt, "ShapeNoise3D");
        }
        finally
        {
            rt.Release();
        }
    }

    [MenuItem("Tools/Volumetric Clouds/Bake Detail Noise")]
    public static void BakeDetail()
    {
        const int res = 64;
        var rt = CreateVolumeRT(res);
        try
        {
            RunWorleyChannel(rt, res, channel: 0, seed: 44, cellsA: 16, cellsB: 32, cellsC: 64, persistence: 0.5f, invert: true);
            RunWorleyChannel(rt, res, channel: 1, seed: 55, cellsA: 32, cellsB: 64, cellsC: 128, persistence: 0.5f, invert: true);
            RunWorleyChannel(rt, res, channel: 2, seed: 66, cellsA: 48, cellsB: 96, cellsC: 192, persistence: 0.5f, invert: true);
            // A 通道也填 Worley（渲染只采样 .rgb，填 A 仅为让 Inspector 预览不透明）
            RunWorleyChannel(rt, res, channel: 3, seed: 77, cellsA: 16, cellsB: 32, cellsC: 64, persistence: 0.5f, invert: true);
            SaveTexture3D(rt, "DetailNoise3D");
        }
        finally
        {
            rt.Release();
        }
    }

    [MenuItem("Tools/Volumetric Clouds/Bake Weather Map")]
    public static void BakeWeatherMap()
    {
        const int res = 256;
        var tex = new Texture2D(res, res, TextureFormat.ARGB32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Repeat
        };

        var pixels = new Color[res * res];
        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            float u = x / (float)res;
            float v = y / (float)res;

            // domain warp：让云团边缘更自然（非直线条状）
            float warpX = ValueNoise.Fbm(u * 3f + 17.31f, v * 3f, 4, 0.5f, 101) * 0.2f;
            float warpY = ValueNoise.Fbm(u * 3f + 31.7f, v * 3f + 7.13f, 4, 0.5f, 202) * 0.2f;

            // R = 覆盖率：warp 后低频噪声 + smoothstep 分层（0 晴 ~ 1 阴）
            float coverageNoise = ValueNoise.Fbm(u * 3f + warpX, v * 3f + warpY, 5, 0.5f, 303);
            float coverage = Mathf.SmoothStep(0.35f, 0.68f, coverageNoise);

            // B = 云型：独立低频噪声 0=层云 ~ 1=积云
            float cloudType = ValueNoise.Fbm(u * 2f + 47.7f, v * 2f + 13.3f, 3, 0.5f, 404);

            // G = 吸收率（降雨）：与覆盖率相关（多云区域更易降雨）
            float rainNoise = ValueNoise.Fbm(u * 8f + 41.3f, v * 8f + 53.7f, 3, 0.5f, 505);
            float absorption = coverage * Mathf.SmoothStep(0.35f, 0.7f, rainNoise);

            pixels[x + y * res] = new Color(coverage, absorption, cloudType, 1f);
        }
        tex.SetPixels(pixels);
        tex.Apply();

        Directory.CreateDirectory(k_TexDir);
        var path = k_TexDir + "/WeatherMap.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
        Debug.Log("Saved: " + path);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 生成
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>确定性 2D value noise（哈希晶格 + smoothstep 插值），用于天气图烘焙。</summary>
    private static class ValueNoise
    {
        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)seed;
                h = (h ^ (uint)x) * 0x27d4eb2d;
                h = (h ^ (uint)y) * 0x165667b1;
                h ^= h >> 15;
                return (h & 0x00FFFFFF) / 16777216f;
            }
        }

        public static float Noise(float x, float y, int seed)
        {
            int ix = Mathf.FloorToInt(x);
            int iy = Mathf.FloorToInt(y);
            float fx = x - ix;
            float fy = y - iy;
            fx = fx * fx * (3 - 2 * fx); // smoothstep 插值
            fy = fy * fy * (3 - 2 * fy);

            float v00 = Hash(ix, iy, seed);
            float v10 = Hash(ix + 1, iy, seed);
            float v01 = Hash(ix, iy + 1, seed);
            float v11 = Hash(ix + 1, iy + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(v00, v10, fx), Mathf.Lerp(v01, v11, fx), fy);
        }

        public static float Fbm(float x, float y, int octaves, float persistence, int seed)
        {
            float amp = 1f, freq = 1f, sum = 0f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Noise(x * freq, y * freq, seed + o * 7);
                norm += amp;
                amp *= persistence;
                freq *= 2f;
            }
            return sum / norm;
        }
    }

    private static RenderTexture CreateVolumeRT(int resolution)
    {
        var rt = new RenderTexture(resolution, resolution, 0)
        {
            graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_UNorm,
            volumeDepth = resolution,
            enableRandomWrite = true,
            dimension = TextureDimension.Tex3D,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            name = "CloudNoiseVolume"
        };
        rt.Create();
        return rt;
    }

    private static void RunWorleyChannel(RenderTexture rt, int resolution, int channel, int seed,
        int cellsA, int cellsB, int cellsC, float persistence, bool invert)
    {
        var compute = LoadCompute("NoiseGenCompute.compute");
        var kernelWorley = compute.FindKernel("CSWorley");
        var kernelNormalize = compute.FindKernel("CSNormalize");

        compute.SetFloat("persistence", persistence);
        compute.SetInt("resolution", resolution);
        compute.SetBool("invertNoise", invert);
        compute.SetInt("tile", 1);
        compute.SetVector("channelMask", ChannelMask(channel));
        compute.SetInt("numCellsA", cellsA);
        compute.SetInt("numCellsB", cellsB);
        compute.SetInt("numCellsC", cellsC);
        compute.SetTexture(kernelWorley, "Result", rt);

        var buffers = new[]
        {
            CreatePointsBuffer(seed, cellsA),
            CreatePointsBuffer(seed + 1, cellsB),
            CreatePointsBuffer(seed + 2, cellsC)
        };
        try
        {
            compute.SetBuffer(kernelWorley, "pointsA", buffers[0]);
            compute.SetBuffer(kernelWorley, "pointsB", buffers[1]);
            compute.SetBuffer(kernelWorley, "pointsC", buffers[2]);

            var minMax = new ComputeBuffer(2, sizeof(int));
            minMax.SetData(new[] { int.MaxValue, 0 });
            try
            {
                compute.SetBuffer(kernelWorley, "minMax", minMax);
                compute.SetBuffer(kernelNormalize, "minMax", minMax);
                compute.SetTexture(kernelNormalize, "Result", rt);

                int groups = Mathf.CeilToInt(resolution / (float)k_Threads);
                compute.Dispatch(kernelWorley, groups, groups, groups);
                compute.Dispatch(kernelNormalize, groups, groups, groups);
            }
            finally
            {
                minMax.Release();
            }
        }
        finally
        {
            foreach (var b in buffers) b.Release();
        }
    }

    private static void RunPerlinWorley(RenderTexture rt, int resolution, int channel, int seed, int numCells)
    {
        var compute = LoadCompute("PerlinWorleyGen.compute");
        int kernel = compute.FindKernel("CSMain");

        compute.SetInt("resolution", resolution);
        compute.SetInt("numCells", numCells);
        compute.SetVector("channelMask", ChannelMask(channel));
        compute.SetTexture(kernel, "Result", rt);

        var points = CreatePointsBuffer(seed, numCells);
        int groups = Mathf.CeilToInt(resolution / (float)k_Threads);
        try
        {
            compute.SetBuffer(kernel, "points", points);
            compute.Dispatch(kernel, groups, groups, groups);
        }
        finally
        {
            points.Release();
        }
        // PW 输出已在 kernel 内 saturate 到 [0,1]，无需 CSNormalize（其 minMax 未被本 kernel 更新，
        // 误用会导致全 1）
    }

    private static ComputeBuffer CreatePointsBuffer(int seed, int numCellsPerAxis)
    {
        var prng = new System.Random(seed);
        var points = new Vector3[numCellsPerAxis * numCellsPerAxis * numCellsPerAxis];
        float cellSize = 1f / numCellsPerAxis;
        for (int x = 0; x < numCellsPerAxis; x++)
        for (int y = 0; y < numCellsPerAxis; y++)
        for (int z = 0; z < numCellsPerAxis; z++)
        {
            var randomOffset = new Vector3(
                (float)prng.NextDouble(), (float)prng.NextDouble(), (float)prng.NextDouble()) * cellSize;
            var cellCorner = new Vector3(x, y, z) * cellSize;
            points[x + numCellsPerAxis * (y + z * numCellsPerAxis)] = cellCorner + randomOffset;
        }

        var buffer = new ComputeBuffer(points.Length, sizeof(float) * 3, ComputeBufferType.Structured);
        buffer.SetData(points);
        return buffer;
    }

    private static Vector4 ChannelMask(int channel)
    {
        return new Vector4(
            channel == 0 ? 1 : 0, channel == 1 ? 1 : 0,
            channel == 2 ? 1 : 0, channel == 3 ? 1 : 0);
    }

    private static ComputeShader LoadCompute(string name)
    {
        var path = k_ComputeDir + "/" + name;
        var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
        if (compute == null)
            throw new FileNotFoundException("Compute shader not found: " + path);
        return compute;
    }

    // ═════════════════════════════════════════════════════════════════════
    // 保存（Slicer.compute 逐层切片 → ReadPixels → Texture3D 资产）
    // ═════════════════════════════════════════════════════════════════════

    private static void SaveTexture3D(RenderTexture volume, string assetName)
    {
        var slicer = LoadCompute("Slicer.compute");
        int kernel = slicer.FindKernel("CSMain");
        int resolution = volume.width;

        slicer.SetInt("resolution", resolution);
        slicer.SetTexture(kernel, "volumeTexture", volume);

        var slices = new Texture2D[resolution];
        try
        {
            for (int layer = 0; layer < resolution; layer++)
            {
                var sliceRT = new RenderTexture(resolution, resolution, 0)
                {
                    enableRandomWrite = true,
                    dimension = TextureDimension.Tex2D
                };
                sliceRT.Create();
                try
                {
                    slicer.SetTexture(kernel, "slice", sliceRT);
                    slicer.SetInt("layer", layer);
                    int groups = Mathf.CeilToInt(resolution / 32f);
                    slicer.Dispatch(kernel, groups, groups, 1);

                    var tex2D = new Texture2D(resolution, resolution, TextureFormat.ARGB32, false);
                    var prev = RenderTexture.active;
                    RenderTexture.active = sliceRT;
                    tex2D.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                    RenderTexture.active = prev;
                    tex2D.Apply();
                    slices[layer] = tex2D;
                }
                finally
                {
                    sliceRT.Release();
                }
            }

            var tex3D = new Texture3D(resolution, resolution, resolution, TextureFormat.ARGB32, false)
            {
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Repeat
            };
            var pixels = tex3D.GetPixels();
            for (int z = 0; z < resolution; z++)
            {
                var layerPixels = slices[z].GetPixels();
                for (int x = 0; x < resolution; x++)
                for (int y = 0; y < resolution; y++)
                    pixels[x + resolution * (y + z * resolution)] = layerPixels[x + y * resolution];
            }
            tex3D.SetPixels(pixels);
            tex3D.Apply();

            Directory.CreateDirectory(k_TexDir);
            var path = k_TexDir + "/" + assetName + ".asset";
            AssetDatabase.CreateAsset(tex3D, path);
            Debug.Log("Saved: " + path);
        }
        finally
        {
            foreach (var s in slices)
                if (s != null) Object.DestroyImmediate(s);
        }
    }
}
