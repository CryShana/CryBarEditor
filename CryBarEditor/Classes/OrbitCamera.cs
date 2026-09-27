using System;
using System.Numerics;

namespace CryBarEditor.Classes;

/// <summary>
/// Spherical orbit camera for 3D preview.
/// </summary>
public class OrbitCamera
{
    public const float NearPlane = 0.01f;
    public const float FarPlane = 10000f;
    public const float MinDistance = 0.01f;
    public const float MaxDistance = FarPlane * 0.5f;

    public float Azimuth { get; set; }
    public float Elevation { get; set; }
    public float Distance { get; set; } = 5f;
    public float TargetX { get; set; }
    public float TargetY { get; set; }
    public float TargetZ { get; set; }
    public float Fov { get; set; } = 45f;

    public Matrix4x4 GetViewMatrix()
    {
        var eye = GetEyePosition();
        var target = new Vector3(TargetX, TargetY, TargetZ);
        return Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
    }

    public Matrix4x4 GetViewMatrix(out Vector3 eyePosition)
    {
        eyePosition = GetEyePosition();
        var target = new Vector3(TargetX, TargetY, TargetZ);
        return Matrix4x4.CreateLookAt(eyePosition, target, Vector3.UnitY);
    }

    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        float fovRad = Fov * MathF.PI / 180f;
        return Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspectRatio, NearPlane, FarPlane);
    }

    Vector3 GetEyePosition()
    {
        float azRad = Azimuth * MathF.PI / 180f;
        float elRad = Elevation * MathF.PI / 180f;
        float cosEl = MathF.Cos(elRad);
        float x = TargetX + Distance * cosEl * MathF.Sin(azRad);
        float y = TargetY + Distance * MathF.Sin(elRad);
        float z = TargetZ + Distance * cosEl * MathF.Cos(azRad);
        return new Vector3(x, y, z);
    }

    public void Rotate(float dAzimuth, float dElevation)
    {
        Azimuth += dAzimuth;
        Elevation = Math.Clamp(Elevation + dElevation, -89f, 89f);
    }

    /// <param name="rate">Per-tick multiplicative step (0.1 = 10% per scroll). Larger
    /// values give snappier close-up zoom for top-down views like the scenario map.</param>
    public void Zoom(float delta, float rate = 0.1f)
    {
        float factor = 1f - delta * rate;
        if (!float.IsFinite(factor)) return;

        factor = Math.Clamp(factor, 0.1f, 10f);
        Distance = Math.Clamp(Distance * factor, MinDistance, MaxDistance);
    }

    public void Pan(float dx, float dy)
    {
        // Compute right and up vectors from view matrix
        var view = GetViewMatrix();
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);

        float scale = Distance;
        var offset = right * (dx * scale) + up * (dy * scale);
        TargetX += offset.X;
        TargetY += offset.Y;
        TargetZ += offset.Z;
    }

    /// <summary>
    /// XZ-plane pan: TargetY stays fixed; dy pans along the camera's forward
    /// direction projected to the ground. Suited for top-down map views where
    /// height drift while panning feels disorienting.
    /// </summary>
    public void PanGround(float dx, float dy)
    {
        var view = GetViewMatrix();
        var right = new Vector3(view.M11, view.M21, view.M31);
        var forward = -new Vector3(view.M13, view.M23, view.M33);

        var rightXZ = new Vector3(right.X, 0, right.Z);
        var forwardXZ = new Vector3(forward.X, 0, forward.Z);
        if (rightXZ.LengthSquared() > 1e-6f) rightXZ = Vector3.Normalize(rightXZ);
        if (forwardXZ.LengthSquared() > 1e-6f) forwardXZ = Vector3.Normalize(forwardXZ);

        float scale = Distance;
        var offset = rightXZ * (dx * scale) + forwardXZ * (dy * scale);
        TargetX += offset.X;
        TargetZ += offset.Z;
    }

    public void FitToSphere(float cx, float cy, float cz, float radius)
    {
        TargetX = float.IsFinite(cx) ? cx : 0f;
        TargetY = float.IsFinite(cy) ? cy : 0f;
        TargetZ = float.IsFinite(cz) ? cz : 0f;

        float fovRad = Fov * System.MathF.PI / 180f;
        float fit = radius / System.MathF.Sin(fovRad / 2f) * 1.1f;
        Distance = radius > 0 && float.IsFinite(fit)
            ? Math.Clamp(fit, MinDistance, MaxDistance)
            : 5f;

        Azimuth = 322f;
        Elevation = 23f;
    }
}
