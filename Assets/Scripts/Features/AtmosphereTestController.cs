using UnityEngine;

/// <summary>
/// Runtime controller for testing atmospheric scattering.
/// Place on the Camera. References a Directional Light as the sun.
///
/// Controls:
///   Left-drag            — rotate sun (azimuth / elevation)
///   Right-drag           — rotate camera (pitch / yaw)
///   Scroll wheel         — camera altitude (velocity)
///   WASD                 — move camera (velocity, world-space)
///   Q / E                — move camera down / up
///   1/2/3/4              — sun presets (noon / sunrise / sunset / night)
///   Space                — reset camera position to initial
/// </summary>
public class AtmosphereTestController : MonoBehaviour
{
    [Header("Sun")]
    public Light sunLight;

    [Header("Movement Speed")]
    public float lookSensitivity = 0.2f;
    public float moveSpeed = 10f;
    public float altitudeSpeed = 50f;

    [Header("Sun State")]
    [Range(0f, 360f)]
    public float sunAzimuth = 180f;
    [Range(-10f, 90f)]
    public float sunElevation = 45f;

    // ── Internal state ────────────────────────────────────────────────
    private Vector3 m_PrevMousePos;
    private Vector3 m_InitialPosition;
    private Quaternion m_InitialRotation;

    void Start()
    {
        if (sunLight == null)
            sunLight = RenderSettings.sun;
        if (sunLight == null)
        {
            sunLight = GetComponent<Light>();
            if (sunLight != null)
                RenderSettings.sun = sunLight;
        }

        m_InitialPosition = transform.position;
        m_InitialRotation = transform.rotation;
        m_PrevMousePos = Input.mousePosition;
    }

    void Update()
    {
        HandleInput();
    }

    void HandleInput()
    {
        // ── Sun presets ─────────────────────────────────────────────
        if (Input.GetKeyDown(KeyCode.Alpha1)) { sunElevation = 90f; sunAzimuth = 180f; ApplySun(); }
        if (Input.GetKeyDown(KeyCode.Alpha2)) { sunElevation = 10f; sunAzimuth = 90f;  ApplySun(); }
        if (Input.GetKeyDown(KeyCode.Alpha3)) { sunElevation = 0f;  sunAzimuth = 270f; ApplySun(); }
        if (Input.GetKeyDown(KeyCode.Alpha4)) { sunElevation = -5f; sunAzimuth = 270f; ApplySun(); }
        if (Input.GetKeyDown(KeyCode.Space))
        {
            transform.position = m_InitialPosition;
            transform.rotation = m_InitialRotation;
        }

        // ── Mouse delta (reset on button down) ──────────────────────
        if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
            m_PrevMousePos = Input.mousePosition;

        Vector3 mouseDelta = Input.mousePosition - m_PrevMousePos;
        m_PrevMousePos = Input.mousePosition;

        // ── Left mouse: rotate sun ──────────────────────────────────
        if (Input.GetMouseButton(0))
        {
            sunAzimuth   += mouseDelta.x * lookSensitivity;
            sunElevation -= mouseDelta.y * lookSensitivity;
            sunElevation  = Mathf.Clamp(sunElevation, -10f, 90f);
            if (sunAzimuth > 360f) sunAzimuth -= 360f;
            if (sunAzimuth < 0f)   sunAzimuth += 360f;
            ApplySun();
        }

        // ── Right mouse: rotate camera ──────────────────────────────
        if (Input.GetMouseButton(1))
        {
            transform.Rotate(0f, mouseDelta.x * lookSensitivity, 0f, Space.World);
            transform.Rotate(-mouseDelta.y * lookSensitivity, 0f, 0f, Space.Self);
        }

        // ── Scroll wheel: altitude ──────────────────────────────────
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f)
        {
            Vector3 pos = transform.position;
            float step = Mathf.Max(pos.y * 0.5f, 0.3f);
            pos.y += scroll * step;
            pos.y = Mathf.Max(pos.y, 0.1f);
            transform.position = pos;
        }

        // ── WASD movement (world-space) ─────────────────────────────
        Vector3 move = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) move.z += 1f;
        if (Input.GetKey(KeyCode.S)) move.z -= 1f;
        if (Input.GetKey(KeyCode.A)) move.x -= 1f;
        if (Input.GetKey(KeyCode.D)) move.x += 1f;
        if (Input.GetKey(KeyCode.E)) move.y += 1f;
        if (Input.GetKey(KeyCode.Q)) move.y -= 1f;

        if (move.sqrMagnitude > 0.001f)
        {
            float speed = moveSpeed * Time.deltaTime;
            // Hold Shift for faster movement
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                speed *= 5f;
            transform.Translate(move.normalized * speed, Space.Self);
            // Clamp Y so camera doesn't go below surface
            Vector3 p = transform.position;
            if (p.y < 0.1f) { p.y = 0.1f; transform.position = p; }
        }
    }

    void ApplySun()
    {
        if (sunLight != null)
        {
            float az = sunAzimuth * Mathf.Deg2Rad;
            float el = sunElevation * Mathf.Deg2Rad;
            // Direction FROM sun TO planet (light direction)
            Vector3 sunDir = new Vector3(
                Mathf.Cos(el) * Mathf.Sin(az),
                Mathf.Sin(el),
                Mathf.Cos(el) * Mathf.Cos(az)
            );
            // Atmosphere does: sunDir = -sun.transform.forward
            // So: sun.transform.forward = -sunDir
            sunLight.transform.forward = -sunDir;
        }
    }

    void OnValidate() { }
}
