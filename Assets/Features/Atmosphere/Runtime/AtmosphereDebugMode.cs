namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    ///     Debug visualization mode for atmosphere LUTs.
    ///     When not None, the debug overlay replaces the normal aerial perspective composite.
    /// </summary>
    public enum AtmosphereDebugMode
    {
        None = 0,

        // ── 2D LUTs (fullscreen overlay) ─────────────────────────────
        OpticalDepthLUT = 1, // R=Rayleigh τ, G=Mie τ
        MultiScatteringLUT = 2, // RGB=G_ALL (multi-scattering factor)
        SkyViewLut = 3, // RGB=sky radiance (HDR)

        // ── 3D Aerial Perspective LUT ────────────────────────────────
        AerialPerspectiveLUT_Slice = 10, // Single XY slice at _DebugSliceZ
        AerialPerspectiveLUT_Grid = 11 // 8×8 grid of Z slices
    }
}
