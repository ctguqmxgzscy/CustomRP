// Cloud Ray Marching — Fragment Shader 版（CloudRayMarchTest.compute 的非 compute 移植）
// 与 compute 版逻辑逐行对应：三态射线段（Below/Above/In clouds）+ 固定步长 +
// 自适应形状 march 状态机（云外大步跳过 / 云内细步 + 光锥光照）+ 深度提前退出。
// 用途：挂在全屏 quad / 相机后处理材质上调试，逐像素输出与 compute 一致的结果。
// 输入: _CameraDepthTexture（深度提前退出）
// 输出: SV_Target (RGBA: RGB=散射色, A=云不透明度)

Shader "Custom/CloudRayMarchFragment"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
        }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/Features/VolumetricClouds/Shaders/CloudLighting.hlsl"
            // Blit.hlsl 提供官方全屏三角形 Vert（输出 positionCS + texcoord），
            // 禁止手写 uv = positionCS.xy*0.5+0.5 —— 否则上下颠倒（与 CloudComposite 同约定）
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // https://www.scratchapixel.com/lessons/3d-basic-rendering/minimal-ray-tracer-rendering-simple-shapes/ray-sphere-intersection
            // 求射线与球面交点，返回入点世界坐标（无交点返回 rayOrigin）
            float3 findRayStartPos(float3 rayOrigin, float3 rayDirection, float3 sphereCenter, float radius)
            {
                float3 l = rayOrigin - sphereCenter;
                float a = 1.0;
                float b = 2.0 * dot(rayDirection, l);
                float c = dot(l, l) - pow(radius, 2);
                float D = pow(b, 2) - 4.0 * a * c;
                if (D < 0.0)
                {
                    return rayOrigin;
                }
                if (abs(D) - 0.00005 <= 0.0)
                {
                    return rayOrigin + rayDirection * (-0.5 * b / a);
                }
                // 数值稳定求根（q 形式，避免 -b ± sqrt 相消）
                float q = 0.0;
                if (b > 0.0)
                {
                    q = -0.5 * (b + sqrt(D));
                }
                else
                {
                    q = -0.5 * (b - sqrt(D));
                }
                float h1 = q / a;
                float h2 = c / q;
                float2 t = float2(min(h1, h2), max(h1, h2));
                if (t.x < 0.0)
                {
                    t.x = t.y;
                    if (t.x < 0.0)
                    {
                        return rayOrigin;
                    }
                }
                return rayOrigin + t.x * rayDirection;
            }

            // ── 输入参数（由 C# 或 Global 每帧 SetGlobal 填充，与 compute 版同名）──
            float _CloudLightingMode;
            // 光照模式（VolumetricCloudFeature.CloudLightingMode）：0=BeerLambert 光锥，1=Hillaire MS 方式 B

            // ── Main Fragment ──────────────────────────────────────────────────
            float4 frag(Varyings input) : SV_Target
            {
                // UV 保持官方约定（v=0 底部）；GetViewDir/ComputeWorldSpacePosition 内部各自按
                // UNITY_UV_STARTS_AT_TOP 处理翻转，这里不要动 uv
                float2 uv = input.texcoord;

                // 深度纹理（fragment 采样带硬件插值，与 compute 的 Load 整数坐标等价）
                // float rawDepth = LoadSceneDepth(uv);
                float rawDepth = SampleSceneDepth(uv);
                // 相机在世界坐标（世界 y = 海拔）；行星中心跟随相机水平位置（精度方案：
                // 相交在球坐标算 t（r 量级），密度采样在世界坐标（海拔小量，float32 高精度））
                float3 position = _WorldSpaceCameraPos;
                // ── 视线方向重建（参考实现思路：逆投影 → 相机空间方向 → 逆视图 → 世界方向）──
                // 参考用 unity_CameraInvProjection / unity_CameraToWorld（内置 RP 变量）；
                // URP 等价：UNITY_MATRIX_I_P（逆投影）/ UNITY_MATRIX_I_V（逆视图）。
                // 不用 GetViewDir 的 UNITY_MATRIX_I_VP——DrawProcedural 全屏渲染下
                // I_VP 与 Blit uv 的组合可能出错，分步重建只依赖相机全局矩阵，更稳。
                float2 ndc = uv * 2.0 - 1.0;
                #if UNITY_UV_STARTS_AT_TOP
                ndc.y = -ndc.y;
                #endif
                float nearDepth = UNITY_REVERSED_Z ? 1.0 : 0.0;
                float4 viewPos = mul(UNITY_MATRIX_I_P, float4(ndc, nearDepth, 1.0));
                float3 dirCS = viewPos.xyz / max(viewPos.w, 1e-6); // 相机空间方向（透视除法）
                float3 viewDir = normalize(mul((float3x3)UNITY_MATRIX_I_V, dirCS)); // 世界方向
                float3 sphereCenter = float3(0, -_PlanetRadius, 0);
                RayMarchInfo info = GetRayMarchInfo();
                info.planetCenter = sphereCenter;

                float3 ro = position;
                float3 rd = viewDir;
                float3 rs;
                float3 re;

                float steps;
                float stepSize;

                // ── 三态射线段（参考实现 ALLOW_IN_CLOUDS）────────────────────
                // 根据相机相对云层球壳的位置（下方/上方/内部）选择 march 区间：
                //   Below：rs = 云底球入点, re = 云顶球入点 → march 覆盖整个云层
                //   Above：rs = 云顶球入点, re = rs + rd×远裁剪面 → 从云顶走到远处
                //   In：   rs = 相机,      re = rs + rd×远裁剪面 → 从相机走到远处
                // steps = lerp(_ShapeStepCount, ×0.5, rd.y)：视线越朝上步数越少（朝上穿云层薄）
                // stepSize = (re-rs)/steps：固定步长，由相机相对云层的位置决定
                bool aboveClouds = false;
                float distanceCameraPlanet = distance(position, sphereCenter);
                if (distanceCameraPlanet < info.planetRadius + info.cloudHeightRangeMinMax.x) // Below clouds
                {
                    rs = findRayStartPos(ro, rd, sphereCenter, info.planetRadius + info.cloudHeightRangeMinMax.x);
                    if (rs.y < 0) // 视线起点在地平线以下 → 无云
                    {
                        return 0.0;
                    }
                    re = findRayStartPos(ro, rd, sphereCenter, info.planetRadius + info.cloudHeightRangeMinMax.y);
                    steps = lerp(_ShapeStepCount, _ShapeStepCount * 0.5, rd.y);
                    stepSize = (distance(re, rs)) / steps;
                }
                else if (distanceCameraPlanet > info.planetRadius + info.cloudHeightRangeMinMax.y) // Above clouds
                {
                    rs = findRayStartPos(ro, rd, sphereCenter, info.planetRadius + info.cloudHeightRangeMinMax.y);
                    re = rs + rd * _ProjectionParams.z;
                    steps = lerp(_ShapeStepCount, _ShapeStepCount * 0.5, rd.y);
                    stepSize = (distance(re, rs)) / steps;
                    aboveClouds = true;
                }
                else // In clouds
                {
                    rs = ro;
                    re = rs + rd * _ProjectionParams.z;

                    steps = lerp(_ShapeStepCount, _ShapeStepCount * 0.5, rd.y);
                    stepSize = (distance(re, rs)) / steps;
                }

                // 入点/出点距离（与 march 循环的 t 同参考系：以相机为基准的射线距离）
                float dstToCloud = distance(rs, ro);
                float endPos = distance(re, ro);
                float tExit = endPos;
                // ── 参考实现深度转换（视空间 z 距离，米）──────────────────────
                // 深度纹理（非线性）→ 线性 0~1 → × 远裁剪面 = 视空间 z 距离
                // far 深度（无几何）映射到 100×远裁剪面 → 天空像素不裁剪云（粗糙 hack）
                // 注意：视空间 z ≠ 射线距离 t，斜视边缘偏差 1/cosθ（参考未修正）
                float depthRef = Linear01Depth(rawDepth, _ZBufferParams);
                if (depthRef == 1.0)
                {
                    depthRef = 100.0;
                }
                depthRef *= _ProjectionParams.z;

                // 获取灯光信息
                Light mainLight = GetMainLight();
                float3 lightDir = normalize(mainLight.direction);

                // 入点抖动（防切片）：fragment 中 InterleavedGradientNoise 需像素坐标
                float IGN = InterleavedGradientNoise(uv * _ScreenParams.xy, _FrameCount);

                float cosTheta = dot(viewDir, lightDir);
                // 每 octave 相位（Hillaire MS，CloudLighting.hlsl）：
                // octave 0 = 双 lobe max（主前向 + 银边）——单散射峰值，保留银边；
                // 高阶 octave 仅主 lobe，偏心率按 _MSEccentricity^o 衰减（多次散射趋于各向同性）
                float3 phase;
                phase[0] = GetDirectScatterProbability(_MieG, cosTheta);
                phase[1] = HenyeyGreenstein(cosTheta, _MieG * _MSEccentricity);
                phase[2] = HenyeyGreenstein(cosTheta, _MieG * _MSEccentricity * _MSEccentricity);

                float3 color = float3(0, 0, 0);
                float tau_PA = 0;
                // ── 自适应形状 march（Nubis 2017 状态机）──────────────────────
                // 云外：判断采样（cheap，跳高频细节）+ 大步跳过（2×步长），
                //       检测到云后退一步防越界，切细步进；
                // 云内：细步进全采样 + 圆锥光照；连续零密度 ≥ 11 判定穿出云层，切回大步。
                // 入点 + 抖动（≤1 步）
                float t = dstToCloud + stepSize * IGN;
                bool inCloud = false;
                int zeroDensityCount = 0;
                float prevDensity = -1.0;
                float alpha = 0.0;

                for (int i = 0; i < (int)steps && alpha <= 1.0 && t < tExit; i++)
                {
                    if (!inCloud)
                    {
                        // 云外：判断采样（cheap，跳过高频细节噪声）+ 大步跳过
                        float3 probeWS = position + viewDir * (t + stepSize);

                        if (distance(position, probeWS) >= depthRef || alpha >= 0.99)
                        {
                            // check if is behind some geometrical object or that cloud color aplha is almost 1
                            break; // if it is then raymarch ends
                        }

                        if (SampleCloudDensity(probeWS, info, true).density <= 0.0)
                        {
                            // 空区大步跳过：初始空区（t < 入点）直接逼近云层入点——
                            // 高空云顶边缘带空区可达数百 km，一步步跳（2×步长）会耗尽
                            // 步数预算 → 云顶边缘像素截断 → 锯齿阶梯；已进入过云层
                            // （t > 入点，云层断裂区）保持原大步，避免倒退死循环。
                            // 钳制到入点前一个细步：march 起点连续 → 边缘长条状破裂/乱抖
                            t = (t < dstToCloud) ? min(t + stepSize * 2.0, dstToCloud - stepSize) : t + stepSize * 2.0;
                            continue;
                        }
                        // 检测到云：后退一步，切细步进
                        t -= stepSize;
                        inCloud = true;
                        zeroDensityCount = 0;
                        prevDensity = -1.0;
                        continue;
                    }

                    // 云内：细步进全采样（含高频细节侵蚀）
                    // 采样位置按步长抖动防切片伪影（Nubis³）：<250m 逐帧随机（消除切片），
                    // ≥250m 静态哈希（时间稳定防闪烁）——IG 噪声固定帧号 0 即静态
                    float jitterHash = t < 250.0
                           ? InterleavedGradientNoise(uv * _ScreenParams.xy, _FrameCount)
                           : InterleavedGradientNoise(uv * _ScreenParams.xy, 0);
                    float3 posWS = position + viewDir * (t + jitterHash * stepSize);
                    CloudInfo cloudInfo = SampleCloudDensity(posWS, info, false);
                    float absorption = cloudInfo.absorptivity;
                    float density = cloudInfo.density;

                    // 零密度计数（连续两次零才 +1，计数只增不减，Nubis 出云判定）
                    if (density <= 0.0 && prevDensity <= 0.0)
                        zeroDensityCount++;
                    prevDensity = density;

                    if (zeroDensityCount >= 11)
                    {
                        // 判定已穿出云层 → 切回大步模式
                        inCloud = false;
                        zeroDensityCount = 0;
                        prevDensity = -1.0;
                        continue;
                    }

                    if (density > 0.0)
                    {
                        alpha += density;
                        // 降雨吸收：放大消光（散射不变 → 云更暗），吸收率来自天气图 G 通道
                        float extinction = _ExtinctionCoeff * lerp(1.0, _AbsorptionStrength, absorption);
                        tau_PA += extinction * density * stepSize;
                        float T_PA = exp(-tau_PA);

                        // ── 圆锥采样（点到光源光学深度 dl，Nubis 2017 Cone Sampling）──
                        // 6 个噪声方向使锥形样本错开（抗对齐伪影），半径随步进递增 → 锥形；
                        // 累积密度 < 0.3 用全采样，超过切 cheap（参考的 0.3 阈值，优化）
                        static float3 noise_kernel[6] =
                        {
                            float3(0.38051305, 0.92453449, -0.02111345),
                            float3(-0.50625799, -0.03590792, -0.86163418),
                            float3(-0.32509218, -0.94557439, 0.01428793),
                            float3(0.09026238, -0.27376545, 0.95755165),
                            float3(0.28128598, 0.42443639, -0.86065785),
                            float3(-0.16852403, 0.14748697, 0.97460106)
                        };

                        float3 samplePos = posWS; // 保存当前采样点（cone 循环推进自己的副本）
                        // 光步长固定 _LightStepLength（参考 sampleConeToLight）——视步长在地平线处
                        // 暴涨，光步跟它走会让 τ_CP 爆炸（云发黑）；锥形半径用 _LightConeRadius × (j+1)
                        float3 lightStep = _LightStepLength * lightDir;
                        float tau_CP = 0.0;

                        float3 conePos = samplePos;
                        for (int j = 0; j < 6; j++) // j < 6，避免 noise_kernel 越界
                        {
                            // 参考偏移：noise_kernel[j] × 光步长 × 锥形半径 × (j+1)（半径随步进递增 → 锥形）
                            float3 p = conePos + noise_kernel[j] * _LightStepLength * _LightConeRadius * float(j + 1);
                            // 累积密度 < 0.3 全采样，超过切 cheap（struct 不能用于三元，用 if/else）
                            CloudInfo coneInfo;
                            if (density < 0.3)
                                coneInfo = SampleCloudDensity(p, info, false);
                            else
                                coneInfo = SampleCloudDensity(p, info, true);
                            // 光学深度按光步长累积（与锥形推进一致）
                            tau_CP += coneInfo.density * _LightStepLength * extinction;
                            conePos += lightStep;
                        }

                        // T_CP：大气段（点到光源 LUT）——云段单散射透射 exp(-τ_CP) 已在 J 内算完
                        float3 T_CP = EvaluateAtmosphereTransmittance(samplePos, sphereCenter, lightDir);
                        float heightFraction = GetHeightFractionForPoint(sphereCenter, info.planetRadius, samplePos,
                         info.cloudHeightRangeMinMax);

                        // ── 光照模式（_CloudLightingMode，Inspector VolumetricCloudFeature → Lighting Mode）──
                        if (_CloudLightingMode < 0.5)
                        {
                            // 模式 0：BeerLambert 光锥（参考 calculateLightEnergy）：beerLaw 吃锥形光路累积密度
                            //（自阴影），powder 吃当前点密度；双 HG 相位 max(HG(fwd), HG(-bwd))×0.6909+0.8；
                            // 大气段透射 T_CP（查 _OpticalDepthLUT）：太阳光穿过大气到达云点的衰减，
                            // 不加会导致低太阳角时云比天空亮、日落不变红
                            float3 lighting = 2 * BeerLambert(tau_CP, _Density) * PowderEffect(density, cosTheta) *
                                GetDualHGPhase(cosTheta) * _SunColor * _SunIntensity * T_CP * 0.3;
                            // 大气 LUT 环境光（与天空盒同源）：云顶受天顶天空照亮（upwardAO：
                            // 光路光学深度 × sin(太阳仰角) 估算上方云遮蔽，HP 思路）；云底受地面方向照亮，随高度混合
                            float3 ambientTop = SampleSkyViewLut(float3(0, 1, 0));
                            float3 ambientBottom = SampleSkyViewLut(float3(0, -1, 0));
                            float sinElev = max(lightDir.y, 0.05);
                            float upwardAO = exp(-tau_CP * sinElev);
                            float3 ambientLighting =
                                (ambientTop * upwardAO + ambientBottom * (1.0 - heightFraction)) * _AmbientLightFactor;
                            // 累积权重：本步新增不透明度 Δτ = 1-exp(-σt·ρ·ds)
                            float stepOpacity = 1.0 - exp(-extinction * density * stepSize);
                            color += (lighting + ambientLighting) * T_PA * stepOpacity;
                        }
                        else
                        {
                            // 模式 1：Hillaire MS 方式 B——对齐大气 scatter += I × (J × T_CP + ms) × T_PA × ds
                            // J = 单散射项 = 散射核 × phase[0]（含银边）× exp(-τ_CP)（EvaluateCloudInScattering）；
                            // kernel（out 参数）：散射核（σs·ρ·S），J 与 ms 共享（HDRP 同构）
                            // ms = 多次散射补益项：octave 从 1 起（octave 0 已在 J 内），薄云 τ_CP→0 时 ms 随 kernel 归零
                            float kernel;
                            float J = EvaluateCloudInScattering(density, _ScatterCoeff, phase, tau_CP, stepSize,
          heightFraction, kernel);
                            float3 ms = 0;
                            [unroll]
                            for (int o = 1; o < NUM_MULTI_SCATTERING_OCTAVES; ++o)
                            {
                                float att = pow(_MSAttenuation, (float)o);
                                float con = pow(_MSContribution, (float)o);
                                ms += kernel * exp(-tau_CP * att) * phase[o] * con;
                            }
                            color += _SunIntensity * _SunColor * (J * T_CP + ms) * T_PA * stepSize;
                        }
                        if (T_PA <= 0.01)
                        {
                            break;
                        }
                    }
                    t += stepSize;
                }

                // alpha 增强（艺术补偿）：消光参数量级低（_ExtinctionCoeff 0.0035 × density≤0.1 →
                // 8km 厚云 τ_a≤2.8 → alpha≤0.94，均值 alpha 仅 0.5-0.8），合成 lerp 时透出大气天空。
                // 物理 σt 应高 10-100 倍，但直接调 extinction 会同步压暗光照（Beer 自阴影），
                // 故仅增强 alpha 输出：τ_a × 3 → 全密云 alpha≈1，光照路径不动。
                return float4(color, 1.0 - exp(-tau_PA * 3.0));
            }
            ENDHLSL
        }
    }
}