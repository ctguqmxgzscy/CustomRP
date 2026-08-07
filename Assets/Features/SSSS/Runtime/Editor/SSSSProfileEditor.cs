using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SSSSProfile))]
public class SSSSProfileEditor : Editor
{
    const int TexSize = 128;
    const float RMax = 8f;
    const float GraphHeight = 200f;

    Texture2D _preview;
    float[] _cachedWeights;
    float[] _cachedVariances;
    float _cachedScale;
    float _cachedNearScale;
    float _cachedFarScale;
    float _cachedBalance;
    bool  _cachedDual;
    Color _cachedFalloff;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var profile = (SSSSProfile)target;
        float[] weights   = { 0.100f, 0.118f, 0.113f, 0.358f, 0.078f };
        float[] variances = { 0.0484f, 0.187f, 0.567f, 1.99f, 7.41f };
        float scale = profile.scatterScale;
        float nearScale = profile.nearScatterScale;
        float farScale  = profile.farScatterScale;
        float balance   = profile.nearFarBalance;
        bool  dual      = profile.useDualScale;
        Color falloff = profile.falloff;

        if (weights == null || variances == null || weights.Length < 5 || variances.Length < 5)
            return;

        bool dirty = _preview == null || DirtyCheck(weights, variances, scale, nearScale, farScale, balance, dual, falloff);
        if (dirty)
            BuildPreview(weights, variances, scale, nearScale, farScale, balance, dual, falloff);

        GUILayout.Space(8);
        GUILayout.Label("Diffusion Profile (per-channel radial)", EditorStyles.boldLabel);

        var rect = GUILayoutUtility.GetRect(GraphHeight, GraphHeight);
        rect = EditorGUI.IndentedRect(rect);
        rect.width = rect.height;

        EditorGUI.DrawRect(rect, new Color(0.08f, 0.08f, 0.08f));

        if (_preview != null)
            GUI.DrawTexture(rect, _preview, ScaleMode.ScaleToFit, false);

        // Legend
        GUILayout.BeginHorizontal();
        DrawLegend(Color.red, "R — longest scatter");
        DrawLegend(Color.green, "G");
        DrawLegend(new Color(0.3f, 0.5f, 1f), "B — shortest");
        GUILayout.EndHorizontal();
    }

    void BuildPreview(float[] weights, float[] variances, float scale, float nearScale, float farScale, float balance, bool dual, Color falloff)
    {
        if (_preview == null)
        {
            _preview = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false);
            _preview.filterMode = FilterMode.Bilinear;
            _preview.wrapMode = TextureWrapMode.Clamp;
        }

        float cx = TexSize / 2f;
        float cy = TexSize / 2f;
        float maxR = 0f, maxG = 0f, maxB = 0f;
        float[][] channels = new float[3][];
        for (int c = 0; c < 3; c++)
            channels[c] = new float[TexSize * TexSize];

        float fc = 1f / (0.001f + falloff.r);
        float fg = 1f / (0.001f + falloff.g);
        float fb = 1f / (0.001f + falloff.b);

        // Evaluate per-channel with falloff
        for (int y = 0; y < TexSize; y++)
        {
            for (int x = 0; x < TexSize; x++)
            {
                float dx = (x - cx) / cx * RMax;
                float dy = (y - cy) / cy * RMax;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                int idx = y * TexSize + x;

                float vr, vg, vb;
                if (dual)
                {
                    float nearInv = 1f / Mathf.Max(nearScale, 0.001f);
                    float farInv  = 1f / Mathf.Max(farScale,  0.001f);
                    vr = Mathf.Lerp(EvalProfile(r * fc * farInv, weights, variances),
                                    EvalProfile(r * fc * nearInv, weights, variances), balance);
                    vg = Mathf.Lerp(EvalProfile(r * fg * farInv, weights, variances),
                                    EvalProfile(r * fg * nearInv, weights, variances), balance);
                    vb = Mathf.Lerp(EvalProfile(r * fb * farInv, weights, variances),
                                    EvalProfile(r * fb * nearInv, weights, variances), balance);
                }
                else
                {
                    float invScale = 1f / Mathf.Max(scale, 0.001f);
                    vr = EvalProfile(r * fc * invScale, weights, variances);
                    vg = EvalProfile(r * fg * invScale, weights, variances);
                    vb = EvalProfile(r * fb * invScale, weights, variances);
                }

                channels[0][idx] = vr;
                channels[1][idx] = vg;
                channels[2][idx] = vb;

                if (vr > maxR) maxR = vr;
                if (vg > maxG) maxG = vg;
                if (vb > maxB) maxB = vb;
            }
        }

        var pixels = new Color[TexSize * TexSize];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color(
                channels[0][i] / maxR,
                channels[1][i] / maxG,
                channels[2][i] / maxB, 1f);

        _preview.SetPixels(pixels);
        _preview.Apply();

        _cachedWeights = (float[])weights.Clone();
        _cachedVariances = (float[])variances.Clone();
        _cachedScale = scale;
        _cachedNearScale = nearScale;
        _cachedFarScale = farScale;
        _cachedBalance = balance;
        _cachedDual = dual;
        _cachedFalloff = falloff;
    }

    float EvalProfile(float r, float[] weights, float[] variances)
    {
        float sum = 0f;
        for (int i = 0; i < 5; i++)
        {
            float g = Mathf.Exp(-(r * r) / (2f * variances[i])) / (2f * Mathf.PI * variances[i]);
            sum += weights[i] * g;
        }
        return sum;
    }

    bool DirtyCheck(float[] w, float[] v, float s, float ns, float fs, float bal, bool dual, Color f)
    {
        if (_cachedWeights == null || _cachedVariances == null) return true;
        if (_cachedScale != s) return true;
        if (_cachedNearScale != ns) return true;
        if (_cachedFarScale != fs) return true;
        if (_cachedBalance != bal) return true;
        if (_cachedDual != dual) return true;
        if (_cachedFalloff != f) return true;
        for (int i = 0; i < 5; i++)
        {
            if (_cachedWeights[i] != w[i]) return true;
            if (_cachedVariances[i] != v[i]) return true;
        }
        return false;
    }

    void DrawLegend(Color color, string label)
    {
        var old = GUI.color;
        GUI.color = color;
        GUILayout.Label($"■ {label}", GUILayout.Width(140));
        GUI.color = old;
    }
}
