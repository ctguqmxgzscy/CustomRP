using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 逐物体高质量阴影的数据源：向 <see cref="HighQualityShadowFeature"/> 提供角色在世界空间的包围盒。
///
/// 矩阵计算、阴影图渲染全部在 Feature 里；本组件只负责「角色现在占据哪块空间」，
/// 并在编辑器里画出包围盒，替代早期版本运行时创建 8 个 primitive 球的做法。
/// </summary>
[DisallowMultipleComponent]
public class HighQualityShadow : MonoBehaviour
{
    [Tooltip("角色根节点，其下所有 Renderer 的包围盒会被合并")]
    public Transform shadowCaster;

    [Tooltip("主平行光。留空则用场景里的主光（RenderSettings.sun）")]
    public Light mainLight;

    [Tooltip("阴影体积沿光线方向向两侧外扩的距离（世界单位）。接收面（地面）在角色背后，不外扩会落在阴影体积之外。")]
    [Min(0f)] public float shadowClipDistance = 30f;

    [Tooltip("强制 SkinnedMeshRenderer.updateWhenOffscreen = true，让包围盒跟随动画。关闭可省一点 CPU，但动画起来后包围盒会停在绑定姿态。")]
    public bool trackAnimatedBounds = true;

    private static readonly List<HighQualityShadow> s_Active = new List<HighQualityShadow>();

    /// <summary>场景中所有启用中的实例（Feature 从这里取数据源）。</summary>
    public static IReadOnlyList<HighQualityShadow> Active => s_Active;

    private readonly List<Vector3> m_Corners = new List<Vector3>(8);
    private readonly List<Renderer> m_Renderers = new List<Renderer>();
    private Bounds m_Bounds;
    private bool m_BoundsValid;

    public Bounds Bounds => m_Bounds;
    public Light ResolvedLight => mainLight != null ? mainLight : RenderSettings.sun;

    private void OnEnable()
    {
        if (!s_Active.Contains(this))
            s_Active.Add(this);

        CollectRenderers();
        RefreshBounds();
    }

    private void OnDisable()
    {
        s_Active.Remove(this);
    }

    private void CollectRenderers()
    {
        m_Renderers.Clear();

        if (shadowCaster == null)
            return;

        shadowCaster.GetComponentsInChildren(true, m_Renderers);

        // 编辑模式下改这个开关会污染场景序列化数据，只在运行时动它
        if (!trackAnimatedBounds || !Application.isPlaying)
            return;

        for (int i = 0; i < m_Renderers.Count; i++)
        {
            if (m_Renderers[i] is SkinnedMeshRenderer skinned && !skinned.updateWhenOffscreen)
                skinned.updateWhenOffscreen = true;
        }
    }

    /// <summary>重算世界空间包围盒。由 Feature 在渲染前调用，保证不晚一帧。</summary>
    public bool RefreshBounds()
    {
        if (shadowCaster == null)
        {
            m_BoundsValid = false;
            return false;
        }

        if (m_Renderers.Count == 0)
            CollectRenderers();

        bool first = true;
        Bounds bounds = default;

        for (int i = 0; i < m_Renderers.Count; i++)
        {
            var renderer = m_Renderers[i];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;

            if (first)
            {
                bounds = renderer.bounds;
                first = false;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        if (first)
        {
            m_BoundsValid = false;
            return false;
        }

        m_Bounds = bounds;
        m_BoundsValid = bounds.size.x > 1e-4f && bounds.size.y > 1e-4f && bounds.size.z > 1e-4f;
        return m_BoundsValid;
    }

    /// <summary>把包围盒的 8 个角点写进 corners（世界空间）。</summary>
    public void GetWorldCorners(List<Vector3> corners)
    {
        GetCorners(m_Bounds, corners);
    }

    private void OnDrawGizmosSelected()
    {
        var caster = shadowCaster != null ? shadowCaster : transform;
        Bounds bounds = Application.isPlaying && m_BoundsValid ? m_Bounds : CalculateEditorBounds(caster);

        Gizmos.color = Application.isPlaying && m_BoundsValid ? Color.cyan : new Color(1f, 0.4f, 0.2f);
        Gizmos.DrawWireCube(bounds.center, bounds.size);

        GetCorners(bounds, m_Corners);
        Gizmos.color = Color.yellow;
        for (int i = 0; i < m_Corners.Count; i++)
            Gizmos.DrawWireSphere(m_Corners[i], 0.05f);
    }

    private static Bounds CalculateEditorBounds(Transform root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Bounds(root.position, Vector3.one * 0.1f);

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        return bounds;
    }

    private static void GetCorners(Bounds bounds, List<Vector3> corners)
    {
        corners.Clear();

        Vector3 c = bounds.center;
        Vector3 e = bounds.extents;

        corners.Add(c + new Vector3(e.x, e.y, e.z));
        corners.Add(c + new Vector3(e.x, -e.y, e.z));
        corners.Add(c + new Vector3(e.x, e.y, -e.z));
        corners.Add(c + new Vector3(e.x, -e.y, -e.z));
        corners.Add(c + new Vector3(-e.x, e.y, e.z));
        corners.Add(c + new Vector3(-e.x, -e.y, e.z));
        corners.Add(c + new Vector3(-e.x, e.y, -e.z));
        corners.Add(c + new Vector3(-e.x, -e.y, -e.z));
    }
}
