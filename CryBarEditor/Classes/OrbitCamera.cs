using System;
using System.Numerics;

namespace CryBarEditor.Classes;

/// <summary>
/// Spherical orbit camera for 3D preview.
/// </summary>
public class OrbitCamera
{
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
        return Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspectRatio, 0.01f, 10000f);
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
        Console.WriteLine("Current Azimuth: " + Azimuth + ", Elevation: " + Elevation);
    }

    public void Zoom(float delta)
    {
        float factor = 1f - delta * 0.1f;
        Distance = MathF.Max(0.01f, Distance * factor);
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

    public void FitToSphere(float cx, float cy, float cz, float radius)
    {
        TargetX = cx;
        TargetY = cy;
        TargetZ = cz;
        float fovRad = Fov * System.MathF.PI / 180f;
        Distance = radius > 0 ? radius / System.MathF.Sin(fovRad / 2f) * 1.1f : 5f;
        Azimuth = 142f;
        Elevation = 23f;
    }
}
