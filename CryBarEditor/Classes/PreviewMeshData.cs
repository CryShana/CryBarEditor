using System;
using System.Numerics;
using CryBar.Export;
using CryBar.TMM;

namespace CryBarEditor.Classes;

/// <summary>
/// CPU-side mesh data decoded from TMM, ready for GPU upload.
/// Vertices are interleaved: pos(3) + normal(3) + uv(2) + tangent(4) = 12 floats/vertex, stride 48 bytes.
/// The tangent's w component carries the bitangent sign (+1 or -1).
/// </summary>
public class PreviewMeshData
{
    public const int VertexStrideFloats = 12;
    public const int VertexStrideBytes = VertexStrideFloats * sizeof(float);
    public const int VertexNormalByteOffset = 3 * sizeof(float);
    public const int VertexUvByteOffset = 6 * sizeof(float);
    public const int VertexTangentByteOffset = 8 * sizeof(float);

    public required float[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public required (int Offset, int Count)[] DrawGroups { get; init; }
    public uint[] DrawGroupMaterialIndices { get; init; } = [];
    public PreviewMarker[] Attachments { get; init; } = [];
    public PreviewMarker[] ImpactPoints { get; init; } = [];
    public string[] MaterialNames { get; init; } = [];
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public float CenterZ { get; init; }
    public float Radius { get; init; }
}

public readonly struct PreviewMarker
{
    public required string Name { get; init; }
    public required Vector3 Position { get; init; }
    public required Vector3 AxisX { get; init; }
    public required Vector3 AxisY { get; init; }
    public required Vector3 AxisZ { get; init; }
    public required bool HasOrientation { get; init; }
}

public static class MeshDataBuilder
{
    public static PreviewMeshData? BuildFromTmm(ReadOnlyMemory<byte> tmmBytes, ReadOnlyMemory<byte> tmmDataBytes)
    {
        var tmm = new TmmFile(tmmBytes);
        if (!tmm.Parsed) return null;

        var dataFile = new TmmDataFile(tmmDataBytes, tmm);
        if (!dataFile.Parsed) return null;
        if (dataFile.Vertices is not { } srcVerts || dataFile.Indices is not { } srcIndices) return null;

        var meshGroups = tmm.MeshGroups ?? [];
        int vertexCount = srcVerts.Length;
        int indexCount = srcIndices.Length;

        const int stride = PreviewMeshData.VertexStrideFloats;
        var vertices = new float[vertexCount * stride];

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        for (int i = 0; i < vertexCount; i++)
        {
            var v = srcVerts[i];
            float px = -(float)v.PosX; // negate X for LH->RH
            float py = (float)v.PosY;
            float pz = (float)v.PosZ;

            var (nx, ny, nz) = TbnDecoder.DecodeNormal(v.TbnX, v.TbnY, v.TbnZ);
            nx = -nx;

            var (tx, ty, tz, sign) = TbnDecoder.DecodeTangent(v.TbnX, v.TbnY, v.TbnZ);
            tx = -tx;

            float u = (float)v.U;
            float vCoord = (float)v.V;

            int off = i * stride;
            vertices[off]      = px;
            vertices[off + 1]  = py;
            vertices[off + 2]  = pz;
            vertices[off + 3]  = nx;
            vertices[off + 4]  = ny;
            vertices[off + 5]  = nz;
            vertices[off + 6]  = u;
            vertices[off + 7]  = vCoord;
            vertices[off + 8]  = tx;
            vertices[off + 9]  = ty;
            vertices[off + 10] = tz;
            // X-negation similarity transform flips the TBN determinant, so the bitangent sign inverts.
            // Mirrors GlbExporter.WriteNormalsAndTangents so normal-mapped lighting matches.
            vertices[off + 11] = -sign;

            if (px < minX) minX = px;
            if (py < minY) minY = py;
            if (pz < minZ) minZ = pz;
            if (px > maxX) maxX = px;
            if (py > maxY) maxY = py;
            if (pz > maxZ) maxZ = pz;
        }

        var indices = new uint[indexCount];
        var drawGroups = new (int Offset, int Count)[meshGroups.Length];
        var drawGroupMaterials = new uint[meshGroups.Length];
        for (int g = 0; g < meshGroups.Length; g++)
        {
            var mg = meshGroups[g];
            drawGroupMaterials[g] = mg.MaterialIndex;

            if (vertexCount == 0 || mg.IndexStart >= (uint)indexCount) continue;

            int iStart = (int)mg.IndexStart;
            int iCount = (int)Math.Min(mg.IndexCount, (uint)(indexCount - iStart));
            iCount -= iCount % 3;
            drawGroups[g] = (iStart, iCount);

            ulong vStart = mg.VertexStart;
            for (int i = iStart; i < iStart + iCount; i += 3)
            {
                ulong a = srcIndices[i] + vStart;
                ulong b = srcIndices[i + 2] + vStart;
                ulong c = srcIndices[i + 1] + vStart;

                // Out-of-range triangles stay degenerate (0,0,0) so the GPU never reads past the VBO.
                if (a >= (ulong)vertexCount || b >= (ulong)vertexCount || c >= (ulong)vertexCount) continue;

                indices[i]     = (uint)a;
                indices[i + 1] = (uint)b;
                indices[i + 2] = (uint)c;
            }
        }

        float cx = 0f, cy = 0f, cz = 0f, radius = 1f;
        if (vertexCount > 0)
        {
            cx = (minX + maxX) * 0.5f;
            cy = (minY + maxY) * 0.5f;
            cz = (minZ + maxZ) * 0.5f;
            float dx = maxX - minX;
            float dy = maxY - minY;
            float dz = maxZ - minZ;
            radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz) * 0.5f;
        }

        var (attachments, impactPoints) = BuildMarkers(
            tmm.Attachments ?? [],
            tmm.Bones ?? [],
            tmm.AutoAttachInfo);

        return new PreviewMeshData
        {
            Vertices = vertices,
            Indices = indices,
            DrawGroups = drawGroups,
            DrawGroupMaterialIndices = drawGroupMaterials,
            Attachments = attachments,
            ImpactPoints = impactPoints,
            MaterialNames = tmm.Materials ?? [],
            CenterX = cx,
            CenterY = cy,
            CenterZ = cz,
            Radius = radius
        };
    }

    /// <summary>
    /// Decomposes attachment local matrices into world-space markers (X-negated for LH->RH)
    /// and produces position-only markers for manual impact points.
    /// </summary>
    public static (PreviewMarker[] Attachments, PreviewMarker[] ImpactPoints) BuildMarkers(
        TmmAttachment[] attachments,
        TmmBone[] bones,
        TmmAutoAttachInfo? autoAttach)
    {
        var attachMarkers = new PreviewMarker[attachments.Length];
        for (int i = 0; i < attachments.Length; i++)
        {
            var att = attachments[i];

            // LocalTransformMatrix is 4x3 row-major (12 floats):
            //   [r0c0 r0c1 r0c2 r0c3] -> row 0: axisX, then translation x
            //   [r1c0 r1c1 r1c2 r1c3] -> row 1: axisY, then translation y
            //   [r2c0 r2c1 r2c2 r2c3] -> row 2: axisZ, then translation z
            var m = att.LocalTransformMatrix;
            var localAxisX = new Vector3(m[0], m[4], m[8]);
            var localAxisY = new Vector3(m[1], m[5], m[9]);
            var localAxisZ = new Vector3(m[2], m[6], m[10]);
            var localTranslation = new Vector3(m[3], m[7], m[11]);

            Matrix4x4 local = new Matrix4x4(
                localAxisX.X, localAxisY.X, localAxisZ.X, 0,
                localAxisX.Y, localAxisY.Y, localAxisZ.Y, 0,
                localAxisX.Z, localAxisY.Z, localAxisZ.Z, 0,
                localTranslation.X, localTranslation.Y, localTranslation.Z, 1);

            Matrix4x4 world = local;
            if (att.ParentBoneId >= 0 && att.ParentBoneId < bones.Length)
            {
                var bone = bones[att.ParentBoneId];
                if (bone.WorldSpaceMatrix is { Length: 16 } wm)
                    world = local * MatrixDecomp.ColMajorToMatrix(wm);
            }

            // Apply X-negation similarity transform: F * M * F (F = diag(-1,1,1,1))
            // to keep markers in the same RH space the mesh is rendered in.
            var pos = new Vector3(world.M41, world.M42, world.M43);
            pos.X = -pos.X;

            var ax = NormalizeOrZero(new Vector3(world.M11, world.M12, world.M13));
            var ay = NormalizeOrZero(new Vector3(world.M21, world.M22, world.M23));
            var az = NormalizeOrZero(new Vector3(world.M31, world.M32, world.M33));
            ax.X = -ax.X;
            ay.X = -ay.X;
            az.X = -az.X;

            attachMarkers[i] = new PreviewMarker
            {
                Name = att.Name,
                Position = pos,
                AxisX = ax,
                AxisY = ay,
                AxisZ = az,
                HasOrientation = true
            };
        }

        var impactSrc = autoAttach?.ManualImpactPoints;
        var impactMarkers = impactSrc is null
            ? Array.Empty<PreviewMarker>()
            : new PreviewMarker[impactSrc.Length];

        if (impactSrc != null)
        {
            for (int i = 0; i < impactSrc.Length; i++)
            {
                var pt = impactSrc[i];
                impactMarkers[i] = new PreviewMarker
                {
                    Name = $"ImpactPoint_{i}",
                    Position = new Vector3(-pt[0], pt[1], pt[2]),
                    AxisX = Vector3.UnitX,
                    AxisY = Vector3.UnitY,
                    AxisZ = Vector3.UnitZ,
                    HasOrientation = false
                };
            }
        }

        return (attachMarkers, impactMarkers);
    }

    static Vector3 NormalizeOrZero(Vector3 v)
    {
        float lenSq = v.LengthSquared();
        if (lenSq < 1e-12f) return Vector3.Zero;
        return v / MathF.Sqrt(lenSq);
    }
}
