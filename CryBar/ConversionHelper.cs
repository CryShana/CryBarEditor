using Assimp;

using CryBar.TMM;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;

using System.Globalization;
using System.Text;

namespace CryBar;

/// <summary>
/// Centralizes common file conversion operations (XMB→XML, DDT→TGA)
/// that were previously duplicated across preview, export, and standalone tool code paths.
/// </summary>
public static class ConversionHelper
{
    /// <summary>
    /// Converts XMB binary data to formatted XML text.
    /// Data should already be decompressed before calling this.
    /// </summary>
    /// <returns>Formatted XML string, or null if parsing failed.</returns>
    public static string? ConvertXmbToXmlText(ReadOnlySpan<byte> data)
    {
        var xml = BarFormatConverter.XMBtoXML(data);
        if (xml == null) return null;
        return BarFormatConverter.FormatXML(xml);
    }

    /// <summary>
    /// Converts XMB data to UTF-8 XML bytes, ready for writing to a file.
    /// Data should already be decompressed before calling this.
    /// </summary>
    public static byte[]? ConvertXmbToXmlBytes(ReadOnlySpan<byte> data)
    {
        var text = ConvertXmbToXmlText(data);
        if (text == null) return null;
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Converts DDT image data to TGA bytes.
    /// Data should already be decompressed before calling this.
    /// </summary>
    /// <param name="data">Raw DDT file data (decompressed)</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>TGA bytes, or null if conversion failed.</returns>
    public static async Task<byte[]?> ConvertDdtToTgaBytes(ReadOnlyMemory<byte> data, CancellationToken token = default)
    {
        var ddt = new DDTImage(data);
        using var image = await BarFormatConverter.ParseDDT(ddt, token: token);
        if (image == null) return null;
        return await ImageToTgaBytes(image, token);
    }

    /// <summary>
    /// Saves an ImageSharp image to TGA byte array (32-bit).
    /// </summary>
    public static async Task<byte[]> ImageToTgaBytes(Image<Rgba32> image, CancellationToken token = default)
    {
        using var memory = new MemoryStream();
        await image.SaveAsTgaAsync(memory, new TgaEncoder
        {
            BitsPerPixel = TgaBitsPerPixel.Pixel32
        }, token);
        return memory.ToArray();
    }

    /// <summary>
    /// Converts a TMM+TMM.DATA pair to Wavefront OBJ format.
    /// Positions, UVs, and normals (decoded from TBN) are included.
    /// Faces are grouped by mesh group/material.
    /// </summary>
    /// <param name="tmmData">Raw .tmm file bytes (decompressed).</param>
    /// <param name="tmmDataData">Raw .tmm.data file bytes (decompressed).</param>
    /// <returns>OBJ text as bytes, or null if parsing failed.</returns>
    static bool TryParseTmmPair(ReadOnlyMemory<byte> tmmData, ReadOnlyMemory<byte> tmmDataData,
        out TmmFile tmm, out TmmDataFile dataFile)
    {
        tmm = new TmmFile(tmmData);
        dataFile = default!;
        if (!tmm.Parse()) return false;

        dataFile = new TmmDataFile(tmmDataData, tmm.NumVertices, tmm.NumTriangleVerts, tmm.NumBones > 0);
        return dataFile.Parse() && dataFile.Vertices != null && dataFile.Indices != null;
    }

    public static byte[]? ConvertTmmToObjBytes(ReadOnlyMemory<byte> tmmData, ReadOnlyMemory<byte> tmmDataData)
    {
        if (!TryParseTmmPair(tmmData, tmmDataData, out var tmm, out var dataFile)) return null;
        var vertices = dataFile.Vertices!;
        var indices = dataFile.Indices!;

        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(vertices.Length * 80); // rough pre-allocation
        sb.AppendLine("# Exported from CryBarEditor");
        sb.AppendLine($"# Vertices: {tmm.NumVertices}, Triangles: {tmm.NumTriangleVerts / 3}");
        sb.AppendLine();

        // Write positions, UVs, and normals in separate OBJ sections (single data pass)
        var uvSection = new StringBuilder(vertices.Length * 30);
        var normalSection = new StringBuilder(vertices.Length * 40);

        foreach (var v in vertices)
        {
            float px = (float)v.PosX, py = (float)v.PosY, pz = (float)v.PosZ;
            sb.AppendLine($"v {px.ToString(ic)} {py.ToString(ic)} {pz.ToString(ic)}");

            float u = (float)v.U, vFlipped = 1.0f - (float)v.V;
            uvSection.AppendLine($"vt {u.ToString(ic)} {vFlipped.ToString(ic)}");

            var (nx, ny, nz) = TbnDecoder.DecodeNormal(v.TbnX, v.TbnY, v.TbnZ);
            normalSection.AppendLine($"vn {nx.ToString(ic)} {ny.ToString(ic)} {nz.ToString(ic)}");
        }

        sb.AppendLine();
        sb.Append(uvSection);
        sb.AppendLine();
        sb.Append(normalSection);
        sb.AppendLine();

        // Write faces grouped by mesh group
        int globalVertexOffset = 0;
        for (int g = 0; g < tmm.MeshGroups.Length; g++)
        {
            var mg = tmm.MeshGroups[g];
            var matName = mg.MaterialIndex < tmm.Materials.Length
                ? tmm.Materials[mg.MaterialIndex] : $"material_{mg.MaterialIndex}";

            sb.AppendLine($"g mesh_group_{g}");
            sb.AppendLine($"usemtl {matName}");

            var triCount = mg.IndexCount / 3;
            for (uint t = 0; t < triCount; t++)
            {
                var baseIdx = mg.IndexStart + t * 3;
                if (baseIdx + 2 >= indices.Length) break;

                // OBJ indices are 1-based; add global vertex offset for this mesh group
                var a = indices[baseIdx] + globalVertexOffset + 1;
                var b = indices[baseIdx + 1] + globalVertexOffset + 1;
                var c = indices[baseIdx + 2] + globalVertexOffset + 1;

                sb.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }
            sb.AppendLine();

            globalVertexOffset += (int)mg.VertexCount;
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Converts a TMM+TMM.DATA pair to FBX format via AssimpNet.
    /// Meshes are exported with positions, UVs, and normals grouped by mesh group.
    /// Bone data is included as named nodes in the scene hierarchy.
    /// <para>
    /// RUNTIME NOTE: AssimpNet is a P/Invoke wrapper around the native libassimp library.
    /// The native <c>assimp.dll</c> (win-x64) must be placed alongside the executable at runtime.
    /// The managed build succeeds without it; only the export call fails if the DLL is absent.
    /// </para>
    /// </summary>
    /// <param name="tmmData">Raw .tmm file bytes (decompressed).</param>
    /// <param name="tmmDataData">Raw .tmm.data file bytes (decompressed).</param>
    /// <returns>FBX bytes, or null if parsing or export failed.</returns>
    public static byte[]? ConvertTmmToFbxBytes(ReadOnlyMemory<byte> tmmData, ReadOnlyMemory<byte> tmmDataData)
    {
        if (!TryParseTmmPair(tmmData, tmmDataData, out var tmm, out var dataFile)) return null;
        var vertices = dataFile.Vertices!;
        var indices = dataFile.Indices!;

        var scene = new Scene();
        scene.RootNode = new Node("Root");

        // Build material list
        foreach (var matName in tmm.Materials)
            scene.Materials.Add(new Assimp.Material { Name = matName });

        // Build one Assimp mesh per TMM mesh group
        int globalVertOffset = 0;
        for (int g = 0; g < tmm.MeshGroups.Length; g++)
        {
            var mg = tmm.MeshGroups[g];
            var mesh = new Mesh($"mesh_group_{g}", PrimitiveType.Triangle);
            mesh.MaterialIndex = (int)mg.MaterialIndex;

            // Guard against malformed data before indexing into the vertex array
            if (globalVertOffset + mg.VertexCount > vertices.Length) break;

            // Vertices, UVs, normals for this group
            for (uint vi = 0; vi < mg.VertexCount; vi++)
            {
                var v = vertices[globalVertOffset + vi];

                mesh.Vertices.Add(new Vector3D((float)v.PosX, (float)v.PosY, (float)v.PosZ));

                float u = (float)v.U;
                float vCoord = 1.0f - (float)v.V;
                mesh.TextureCoordinateChannels[0].Add(new Vector3D(u, vCoord, 0));

                var (nx, ny, nz) = TbnDecoder.DecodeNormal(v.TbnX, v.TbnY, v.TbnZ);
                mesh.Normals.Add(new Vector3D(nx, ny, nz));
            }
            mesh.UVComponentCount[0] = 2;

            // Faces (triangles)
            var triCount = mg.IndexCount / 3;
            for (uint t = 0; t < triCount; t++)
            {
                var baseIdx = mg.IndexStart + t * 3;
                if (baseIdx + 2 >= indices.Length) break;

                // Indices are local within this mesh group
                int a = (int)indices[baseIdx];
                int b = (int)indices[baseIdx + 1];
                int c = (int)indices[baseIdx + 2];
                mesh.Faces.Add(new Face([a, b, c]));
            }

            scene.Meshes.Add(mesh);
            int meshIdx = scene.Meshes.Count - 1;

            var meshNode = new Node($"mesh_group_{g}", scene.RootNode);
            meshNode.MeshIndices.Add(meshIdx);
            scene.RootNode.Children.Add(meshNode);

            globalVertOffset += (int)mg.VertexCount;
        }

        // Add bone nodes under root for skeleton reference
        if (tmm.Bones != null && tmm.Bones.Length > 0)
        {
            var skeletonRoot = new Node("Skeleton", scene.RootNode);
            scene.RootNode.Children.Add(skeletonRoot);

            var boneNodes = new Node[tmm.Bones.Length];
            for (int i = 0; i < tmm.Bones.Length; i++)
            {
                boneNodes[i] = new Node(tmm.Bones[i].Name);
            }
            for (int i = 0; i < tmm.Bones.Length; i++)
            {
                var parentId = tmm.Bones[i].ParentId;
                if (parentId >= 0 && parentId < boneNodes.Length)
                    boneNodes[parentId].Children.Add(boneNodes[i]);
                else
                    skeletonRoot.Children.Add(boneNodes[i]);
            }
        }

        try
        {
            using var ctx = new AssimpContext();
            var blob = ctx.ExportToBlob(scene, "fbx");
            return blob?.Data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Determines the converted file extension for a given source extension.
    /// Returns null if no conversion is applicable.
    /// </summary>
    public static string? GetConvertedExtension(string extension, bool tmmToFbx = false)
    {
        return extension.ToLower() switch
        {
            ".xmb" => null, // XMB extension is removed, revealing the underlying extension (e.g. .xml.xmb → .xml)
            ".ddt" => ".tga",
            ".tmm" => tmmToFbx ? ".fbx" : ".obj",
            _ => null
        };
    }

    /// <summary>
    /// Returns true if the file extension supports format conversion during export.
    /// </summary>
    public static bool IsConvertibleExtension(string extension)
    {
        return extension.ToLower() is ".xmb" or ".ddt" or ".tmm";
    }
}
