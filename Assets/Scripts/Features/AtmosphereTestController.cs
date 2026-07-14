using UnityEngine;

/// <summary>
/// Runtime controller for testing atmospheric scattering.
/// Place on the Camera. Uses a Directional Light as the sun.
///
/// Controls:
///   Right-drag          — rotate camera (pitch / yaw)
///   Shift + Right-drag  — move sun (azimuth / elevation)
///   Scroll wheel        — change camera altitude
///   WASD                — move camera along surface
///   1/2/3/4             — sun presets (noon / sunrise / sunset / below horizon)
///   Space               — reset to defaults
/// </summary>
public class AtmosphereTestController : MonoBehaviour
{
    [Header("Camera")]
    [Tooltip("Altitude above planet surface (meters).")]
    [Range(0f, 100000f)]
    public float altitude = 1000f;

    [Tooltip("Latitude on planet surface (degrees). 0 = equator.")]
    [Range(-89f, 89f)]
    public float latitude = 0f;

    [Tooltip("Longitude on planet surface (degrees).")]
    [Range(-180f, 180f)]
    public float longitude = 0f;

    [Tooltip("Camera pitch relative to horizon. 0 = horizontal, 90 = straight up.")]
    [Range(-30f, 90f)]
    public float pitch = 0f;

    [Tooltip("Camera yaw (world-space, degrees). 0 = +Z, 90 = +X.")]
    [Range(0f, 360f)]
    public float yaw = 180f;

    [Header("Sun")]
    [Tooltip("Sun azimuth on the horizon plane (degrees). 0 = north, 90 = east.")]
    [Range(0f, 360f)]
    public float sunAzimuth = 180f;

    [Tooltip("Sun elevation above horizon (degrees). -5 = below horizon, 90 = zenith.")]
    [Range(-10f, 90f)]
    public float sunElevation = 45f;

    [Header("Movement")]
    public float moveSpeed = 200f;
    public float lookSpeed = 60f;
    public float scrollSpeed = 500f;

    [Header("Setup")]
    public Light sunLight;
    public float planetRadius = 6360000f;

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
    }

    void Update()
    {
        HandleInput();
        ApplySun();
        ApplyCamera();
    }

    void HandleInput()
    {
        // ── Sun presets ─────────────────────────────────────────────
        if (Input.GetKeyDown(KeyCode.Alpha1)) { sunElevation = 90f; sunAzimuth = 180f; } // noon
        if (Input.GetKeyDown(KeyCode.Alpha2)) { sunElevation = 10f; sunAzimuth = 90f;  } // sunrise
        if (Input.GetKeyDown(KeyCode.Alpha3)) { sunElevation = 0f;  sunAzimuth = 270f; } // sunset
        if (Input.GetKeyDown(KeyCode.Alpha4)) { sunElevation = -5f; sunAzimuth = 270f; } // night
        if (Input.GetKeyDown(KeyCode.Space))
        {
            altitude = 1000f; pitch = 0f; yaw = 180f;
            latitude = 0f; longitude = 0f;
            sunElevation = 45f; sunAzimuth = 180f;
        }

        // ── Altitude ────────────────────────────────────────────────
        altitude += Input.GetAxis("Mouse ScrollWheel") * scrollSpeed * Time.deltaTime * 50f;
        altitude = Mathf.Max(1f, altitude);

        // ── Camera look (right mouse) ───────────────────────────────
        if (Input.GetMouseButton(1) && !Input.GetKey(KeyCode.LeftShift))
        {
            yaw += Input.GetAxis("Mouse X") * lookSpeed;
            pitch -= Input.GetAxis("Mouse Y") * lookSpeed;
            pitch = Mathf.Clamp(pitch, -30f, 89f);
        }

        // ── Sun angle (Shift + right mouse) ─────────────────────────
        if (Input.GetMouseButton(1) && Input.GetKey(KeyCode.LeftShift))
        {
            sunAzimuth += Input.GetAxis("Mouse X") * lookSpeed;
            sunElevation -= Input.GetAxis("Mouse Y") * lookSpeed;
            sunElevation = Mathf.Clamp(sunElevation, -10f, 90f);
            if (sunAzimuth > 360f) sunAzimuth -= 360f;
            if (sunAzimuth < 0f)   sunAzimuth += 360f;
        }

        // ── WASD surface movement ───────────────────────────────────
        float latMove = Input.GetAxis("Vertical")   * moveSpeed * Time.deltaTime;
        float lonMove = Input.GetAxis("Horizontal") * moveSpeed * Time.deltaTime;
        // Convert meters to degrees on sphere
        float r = planetRadius + altitude;
        latitude  += latMove / r * Mathf.Rad2Deg;
        longitude += lonMove / (r * Mathf.Cos(latitude * Mathf.Deg2Rad)) * Mathf.Rad2Deg;
        latitude  = Mathf.Clamp(latitude,  -89f, 89f);
        if (longitude >  180f) longitude -= 360f;
        if (longitude < -180f) longitude += 360f;
    }

    void ApplySun()
    {
        if (sunLight != null)
        {
            float az = sunAzimuth * Mathf.Deg2Rad;
            float el = sunElevation * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(
                Mathf.Cos(el) * Mathf.Sin(az),
                Mathf.Sin(el),
                Mathf.Cos(el) * Mathf.Cos(az)
            );
            sunLight.transform.forward = -dir;
        }
    }

    void ApplyCamera()
    {
        Camera cam = GetComponent<Camera>();
        if (cam == null) return;

        // Position on sphere, planet center at origin
        float lat = latitude  * Mathf.Deg2Rad;
        float lon = longitude * Mathf.Deg2Rad;
        float r = planetRadius + altitude;

        Vector3 pos = new Vector3(
            r * Mathf.Cos(lat) * Mathf.Cos(lon),
            r * Mathf.Sin(lat),
            r * Mathf.Cos(lat) * Mathf.Sin(lon)
        );

        // Radial outward = local "up"
        Vector3 up = pos.normalized;

        // Forward: world-space yaw (around global Y) then pitch
        float yRad = yaw * Mathf.Deg2Rad;
        float pRad = pitch * Mathf.Deg2Rad;
        Vector3 fwd = new Vector3(
            Mathf.Sin(yRad) * Mathf.Cos(pRad),
            Mathf.Sin(pRad),
            Mathf.Cos(yRad) * Mathf.Cos(pRad)
        );

        cam.transform.position = pos;
        cam.transform.rotation = Quaternion.LookRotation(fwd, up);
    }

    void OnValidate()
    {
        if (Application.isPlaying)
        {
            ApplySun();
            ApplyCamera();
        }
    }
}
