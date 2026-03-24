using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using CryBar;
using CryBar.Bar;
using CryBar.Export;
using CryBar.TMM;

using static CryBar.Tests.TmmTestHelpers;

namespace CryBar.Tests;

public class GlbExporterTests
{
    #region Null / Invalid Input Tests

    [Fact]
    public void ExportGlb_UnparsedTmm_ReturnsNull()
    {
        var tmm = new TmmFile(ReadOnlyMemory<byte>.Empty);
        var dataFile = new TmmDataFile(ReadOnlyMemory<byte>.Empty, tmm);
        var result = GlbExporter.ExportGlb(tmm, dataFile);
        Assert.Null(result);
    }

    [Fact]
    public void ExportGlb_NoVertices_ReturnsNull()
    {
        var tmm = CreateSyntheticTmmFile(0, 0, false);
        Assert.True(tmm.Parsed);

        var dataBytes = CreateSyntheticData(numVertices: 0, numTriangleVerts: 0, hasSkinning: false);
        var dataFile = new TmmDataFile(dataBytes, tmm);
        Assert.True(dataFile.Parsed);

        var result = GlbExporter.ExportGlb(tmm, dataFile);
        Assert.Null(result);
    }

    #endregion

    #region GLB Structure Tests

    [Fact]
    public void ExportGlb_ValidGeometry_ReturnsValidGlbHeader()
    {
        var (tmm, dataFile) = CreateMinimalModel();
        var glb = GlbExporter.ExportGlb(tmm, dataFile);

        Assert.NotNull(glb);
        Assert.True(glb.Length >= 12, "GLB must be at least 12 bytes (header)");

        // Magic: glTF
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(0, 4));
        Assert.Equal(0x46546C67u, magic);

        // Version: 2
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4, 4));
        Assert.Equal(2u, version);

        // Total length matches
        uint totalLength = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8, 4));
        Assert.Equal((uint)glb.Length, totalLength);
    }

    [Fact]
    public void ExportGlb_ValidGeometry_HasJsonChunk()
    {
        var (tmm, dataFile) = CreateMinimalModel();
        var glb = GlbExporter.ExportGlb(tmm, dataFile);
        Assert.NotNull(glb);

        // JSON chunk starts at offset 12
        uint jsonChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        uint jsonChunkType = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16, 4));
        Assert.Equal(0x4E4F534Au, jsonChunkType);

        // JSON should be padded to 4 bytes
        Assert.Equal(0u, jsonChunkLength % 4);

        // JSON content should be parseable
        var jsonBytes = glb.AsSpan(20, (int)jsonChunkLength);
        // Trim trailing spaces
        int jsonEnd = (int)jsonChunkLength;
        while (jsonEnd > 0 && jsonBytes[jsonEnd - 1] == 0x20) jsonEnd--;

        var jsonDoc = JsonDocument.Parse(jsonBytes[..jsonEnd].ToArray());
        var root = jsonDoc.RootElement;

        // Verify required top-level properties
        Assert.True(root.TryGetProperty("asset", out var asset));
        Assert.Equal("2.0", asset.GetProperty("version").GetString());
        Assert.Equal("CryBarEditor", asset.GetProperty("generator").GetString());

        Assert.True(root.TryGetProperty("scene", out _));
        Assert.True(root.TryGetProperty("scenes", out _));
        Assert.True(root.TryGetProperty("nodes", out _));
        Assert.True(root.TryGetProperty("meshes", out _));
        Assert.True(root.TryGetProperty("accessors", out _));
        Assert.True(root.TryGetProperty("bufferViews", out _));
        Assert.True(root.TryGetProperty("buffers", out _));
    }

    [Fact]
    public void ExportGlb_ValidGeometry_HasBinChunk()
    {
        var (tmm, dataFile) = CreateMinimalModel();
        var glb = GlbExporter.ExportGlb(tmm, dataFile)!;

        // BIN chunk follows JSON chunk
        uint jsonChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        int binChunkStart = 20 + (int)jsonChunkLength;

        Assert.True(glb.Length >= binChunkStart + 8, "GLB must have a BIN chunk");

        uint binChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binChunkStart, 4));
        uint binChunkType = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binChunkStart + 4, 4));
        Assert.Equal(0x004E4942u, binChunkType);

        // BIN should be padded to 4 bytes
        Assert.Equal(0u, binChunkLength % 4);

        // Total length should match
        uint expectedTotal = (uint)(12 + 8 + jsonChunkLength + 8 + binChunkLength);
        Assert.Equal(expectedTotal, (uint)glb.Length);
    }

    [Fact]
    public void ExportGlb_ValidGeometry_JsonHasMeshWithPrimitives()
    {
        var (tmm, dataFile) = CreateMinimalModel();
        var glb = GlbExporter.ExportGlb(tmm, dataFile)!;
        var json = ExtractJson(glb);

        var meshes = json.GetProperty("meshes");
        Assert.Equal(1, meshes.GetArrayLength());

        var primitives = meshes[0].GetProperty("primitives");
        Assert.True(primitives.GetArrayLength() >= 1);

        var prim = primitives[0];
        var attrs = prim.GetProperty("attributes");
        Assert.True(attrs.TryGetProperty("POSITION", out _));
        Assert.True(attrs.TryGetProperty("NORMAL", out _));
        Assert.True(attrs.TryGetProperty("TANGENT", out _));
        Assert.True(attrs.TryGetProperty("TEXCOORD_0", out _));
        Assert.True(prim.TryGetProperty("indices", out _));
        Assert.Equal(4, prim.GetProperty("mode").GetInt32());
    }

    #endregion

    #region Skinning Tests

    [Fact]
    public void ExportGlb_WithBones_HasSkinData()
    {
        var (tmm, dataFile) = CreateSkinnedModel();
        var glb = GlbExporter.ExportGlb(tmm, dataFile)!;
        var json = ExtractJson(glb);

        // Should have skin
        Assert.True(json.TryGetProperty("skins", out var skins));
        Assert.Equal(1, skins.GetArrayLength());

        var skin = skins[0];
        Assert.True(skin.TryGetProperty("joints", out var joints));
        Assert.Equal(2, joints.GetArrayLength()); // 2 bones

        Assert.True(skin.TryGetProperty("inverseBindMatrices", out _));
        Assert.True(skin.TryGetProperty("skeleton", out _));

        // Mesh node should reference skin
        var nodes = json.GetProperty("nodes");
        var meshNode = nodes[0];
        Assert.Equal(0, meshNode.GetProperty("skin").GetInt32());

        // Should have bone nodes
        Assert.True(nodes.GetArrayLength() >= 3); // mesh node + 2 bones

        // Bone nodes should have names
        Assert.Equal("bone_0", nodes[1].GetProperty("name").GetString());
        Assert.Equal("bone_1", nodes[2].GetProperty("name").GetString());

        // Primitives should have JOINTS_0 and WEIGHTS_0
        var prim = json.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var attrs = prim.GetProperty("attributes");
        Assert.True(attrs.TryGetProperty("JOINTS_0", out _));
        Assert.True(attrs.TryGetProperty("WEIGHTS_0", out _));
    }

    [Fact]
    public void ExportGlb_WithAttachments_HasAttachmentNodes()
    {
        var tmm = CreateSyntheticTmmFile(3, 3, true,
            numMeshGroups: 1, materials: ["default_mat"], submodels: ["default"],
            numBones: 2, numAttachments: 2);
        Assert.True(tmm.Parsed);
        Assert.Equal(2, tmm.Attachments!.Length);

        var dataBytes = CreateSyntheticData(numVertices: 3, numTriangleVerts: 3, hasSkinning: true);
        var dataFile = new TmmDataFile(dataBytes, tmm);
        Assert.True(dataFile.Parsed);

        var glb = GlbExporter.ExportGlb(tmm, dataFile)!;
        Assert.NotNull(glb);
        var json = ExtractJson(glb);

        var nodes = json.GetProperty("nodes");
        // Node 0 = mesh, nodes 1-2 = bones, nodes 3-4 = attachments
        Assert.Equal(5, nodes.GetArrayLength());

        // Attachment nodes should have names and matrices
        Assert.Equal("attach_0", nodes[3].GetProperty("name").GetString());
        Assert.Equal("attach_1", nodes[4].GetProperty("name").GetString());
        Assert.True(nodes[3].TryGetProperty("matrix", out var mat));
        Assert.Equal(16, mat.GetArrayLength());

        // Bone 0 should have attach_0 as child (node index 3)
        var bone0Children = nodes[1].GetProperty("children");
        bool hasAttachChild = false;
        for (int i = 0; i < bone0Children.GetArrayLength(); i++)
        {
            if (bone0Children[i].GetInt32() == 3)
                hasAttachChild = true;
        }
        Assert.True(hasAttachChild, "Bone 0 should have attachment 0 as child");
    }

    #endregion

    #region Material Tests

    [Fact]
    public void ExportGlb_WithMaterials_HasMaterialData()
    {
        var (tmm, dataFile) = CreateMinimalModel();

        // Create a minimal PNG (1x1 pixel) for testing
        var fakePng = CreateMinimalPng();
        var materials = new List<GlbExporter.GlbMaterial>
        {
            new()
            {
                Name = "test_material",
                BaseColorPng = fakePng,
                NormalMapPng = fakePng
            }
        };

        var glb = GlbExporter.ExportGlb(tmm, dataFile, materials)!;
        var json = ExtractJson(glb);

        // Should have materials
        Assert.True(json.TryGetProperty("materials", out var mats));
        Assert.Equal(1, mats.GetArrayLength());
        Assert.Equal("test_material", mats[0].GetProperty("name").GetString());

        // PBR properties
        var pbr = mats[0].GetProperty("pbrMetallicRoughness");
        Assert.Equal(0, pbr.GetProperty("metallicFactor").GetInt32());
        Assert.Equal(1, pbr.GetProperty("roughnessFactor").GetInt32());
        Assert.True(pbr.TryGetProperty("baseColorTexture", out _));

        // Normal texture
        Assert.True(mats[0].TryGetProperty("normalTexture", out _));

        // Should have textures and images
        Assert.True(json.TryGetProperty("textures", out var textures));
        Assert.Equal(2, textures.GetArrayLength()); // base color + normal

        Assert.True(json.TryGetProperty("images", out var images));
        Assert.Equal(2, images.GetArrayLength());
        Assert.Equal("image/png", images[0].GetProperty("mimeType").GetString());

        Assert.True(json.TryGetProperty("samplers", out _));
    }

    [Fact]
    public void ExportGlb_WithMaterials_EmbedsPngData()
    {
        var (tmm, dataFile) = CreateMinimalModel();
        var fakePng = CreateMinimalPng();

        var materials = new List<GlbExporter.GlbMaterial>
        {
            new()
            {
                Name = "test",
                BaseColorPng = fakePng
            }
        };

        var glb = GlbExporter.ExportGlb(tmm, dataFile, materials)!;

        // The BIN chunk should contain the PNG data
        var binData = ExtractBin(glb);

        // Search for PNG header in bin data
        bool foundPng = false;
        for (int i = 0; i <= binData.Length - 4; i++)
        {
            if (binData[i] == 0x89 && binData[i + 1] == 0x50 &&
                binData[i + 2] == 0x4E && binData[i + 3] == 0x47)
            {
                foundPng = true;
                break;
            }
        }
        Assert.True(foundPng, "BIN chunk should contain embedded PNG data");
    }

    #endregion

    #region ConversionHelper Integration Tests

    [Fact]
    public void ConvertTmmToGlbBytes_ValidInput_ReturnsGlb()
    {
        uint nv = 3, nt = 3;
        uint vbl = nv * (uint)TmmVertex.SizeInBytes;
        var tmmBytes = CreateSyntheticTmm(numMeshGroups: 1, numVertices: nv, numTriangleVerts: nt,
            materials: ["mat1"], submodels: ["default"],
            verticesStart: 0, verticesByteLength: vbl,
            trianglesStart: vbl, trianglesByteLength: nt * 2);
        var dataBytes = CreateSyntheticData(numVertices: nv, numTriangleVerts: nt, hasSkinning: false);

        var result = ConversionHelper.ConvertTmmToGlbBytes(tmmBytes, dataBytes);
        Assert.NotNull(result);

        // Verify GLB magic
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0, 4));
        Assert.Equal(0x46546C67u, magic);
    }

    [Fact]
    public void ConvertTmmToGlbBytes_InvalidInput_ReturnsNull()
    {
        var result = ConversionHelper.ConvertTmmToGlbBytes(ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        Assert.Null(result);
    }

    [Fact]
    public void ConvertTmmToGlbBytes_WithMaterials_ReturnsGlb()
    {
        uint nv = 3, nt = 3;
        uint vbl = nv * (uint)TmmVertex.SizeInBytes;
        var tmmBytes = CreateSyntheticTmm(numMeshGroups: 1, numVertices: nv, numTriangleVerts: nt,
            materials: ["mat1"], submodels: ["default"],
            verticesStart: 0, verticesByteLength: vbl,
            trianglesStart: vbl, trianglesByteLength: nt * 2);
        var dataBytes = CreateSyntheticData(numVertices: nv, numTriangleVerts: nt, hasSkinning: false);

        var materials = new List<GlbExporter.GlbMaterial>
        {
            new() { Name = "mat1" }
        };

        var result = ConversionHelper.ConvertTmmToGlbBytes(tmmBytes, dataBytes, materials);
        Assert.NotNull(result);
    }

    [Fact]
    public void GetConvertedExtension_TmmDefault_ReturnsObj()
    {
        Assert.Equal(".obj", ConversionHelper.GetConvertedExtension(".tmm"));
    }

    [Fact]
    public void GetConvertedExtension_TmmToGltf_ReturnsGlb()
    {
        Assert.Equal(".glb", ConversionHelper.GetConvertedExtension(".tmm", tmmToGltf: true));
    }

    #endregion

    #region Helper Methods

    static (TmmFile tmm, TmmDataFile dataFile) CreateMinimalModel()
    {
        uint numVerts = 3;
        uint numTris = 3; // 1 triangle = 3 indices

        var tmm = CreateSyntheticTmmFile(numVerts, numTris, false,
            numMeshGroups: 1, materials: ["default_mat"], submodels: ["default"]);
        Assert.True(tmm.Parsed);

        var dataBytes = CreateSyntheticData(numVertices: numVerts, numTriangleVerts: numTris, hasSkinning: false);
        var dataFile = new TmmDataFile(dataBytes, tmm);
        Assert.True(dataFile.Parsed);

        return (tmm, dataFile);
    }

    static (TmmFile tmm, TmmDataFile dataFile) CreateSkinnedModel()
    {
        uint numVerts = 3;
        uint numTris = 3;

        var tmm = CreateSyntheticTmmFile(numVerts, numTris, true,
            numMeshGroups: 1, materials: ["default_mat"], submodels: ["default"],
            numBones: 2);
        Assert.True(tmm.Parsed);

        var dataBytes = CreateSyntheticData(numVertices: numVerts, numTriangleVerts: numTris, hasSkinning: true);
        var dataFile = new TmmDataFile(dataBytes, tmm);
        Assert.True(dataFile.Parsed);

        return (tmm, dataFile);
    }

    // TMM builders (CreateSyntheticTmm, CreateSyntheticTmmFile, CreateSyntheticData) are in TmmTestHelpers

    /// <summary>
    /// Creates a minimal valid PNG file (1x1 transparent pixel).
    /// </summary>
    static byte[] CreateMinimalPng()
    {
        // Minimal 1x1 RGBA PNG
        using var ms = new MemoryStream();
        // PNG header
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR chunk
        WriteChunk(ms, "IHDR", w =>
        {
            w.Write(BinaryPrimitives.ReverseEndianness(1)); // width
            w.Write(BinaryPrimitives.ReverseEndianness(1)); // height
            w.Write((byte)8);  // bit depth
            w.Write((byte)6);  // color type: RGBA
            w.Write((byte)0);  // compression
            w.Write((byte)0);  // filter
            w.Write((byte)0);  // interlace
        });

        // IDAT chunk - compressed image data
        // For a 1x1 RGBA image, the raw data is: filter byte (0) + 4 RGBA bytes
        byte[] rawData = [0x00, 0x00, 0x00, 0x00, 0x00]; // filter=none, RGBA=0000
        byte[] compressed;
        using (var compMs = new MemoryStream())
        {
            using (var deflate = new System.IO.Compression.DeflateStream(compMs, System.IO.Compression.CompressionLevel.Fastest, true))
            {
                deflate.Write(rawData);
            }
            compressed = compMs.ToArray();
        }

        // zlib wrapper: CMF + FLG + compressed + adler32
        byte[] zlibData;
        using (var zlibMs = new MemoryStream())
        {
            zlibMs.WriteByte(0x78); // CMF
            zlibMs.WriteByte(0x01); // FLG
            zlibMs.Write(compressed);
            // Adler32 checksum
            uint adler = Adler32(rawData);
            zlibMs.WriteByte((byte)((adler >> 24) & 0xFF));
            zlibMs.WriteByte((byte)((adler >> 16) & 0xFF));
            zlibMs.WriteByte((byte)((adler >> 8) & 0xFF));
            zlibMs.WriteByte((byte)(adler & 0xFF));
            zlibData = zlibMs.ToArray();
        }

        WriteChunk(ms, "IDAT", w => w.Write(zlibData));

        // IEND chunk
        WriteChunk(ms, "IEND", _ => { });

        return ms.ToArray();
    }

    static void WriteChunk(MemoryStream ms, string type, Action<BinaryWriter> writeData)
    {
        using var dataMs = new MemoryStream();
        using var dataW = new BinaryWriter(dataMs);
        writeData(dataW);
        dataW.Flush();
        var data = dataMs.ToArray();
        var typeBytes = Encoding.ASCII.GetBytes(type);

        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(BinaryPrimitives.ReverseEndianness(data.Length)); // length (big endian)
        w.Write(typeBytes); // type
        w.Write(data); // data

        // CRC32 over type + data
        var crcData = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcData, 0);
        data.CopyTo(crcData, typeBytes.Length);
        uint crc = BarCompression.ComputeCrc32(crcData);
        w.Write(BinaryPrimitives.ReverseEndianness((int)crc));
    }

    static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }



    internal static JsonElement ExtractJson(byte[] glb)
    {
        uint jsonChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        var jsonBytes = glb.AsSpan(20, (int)jsonChunkLength);
        int jsonEnd = (int)jsonChunkLength;
        while (jsonEnd > 0 && jsonBytes[jsonEnd - 1] == 0x20) jsonEnd--;
        return JsonDocument.Parse(jsonBytes[..jsonEnd].ToArray()).RootElement;
    }

    static byte[] ExtractBin(byte[] glb)
    {
        uint jsonChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        int binStart = 20 + (int)jsonChunkLength;
        uint binChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binStart, 4));
        return glb.AsSpan(binStart + 8, (int)binChunkLength).ToArray();
    }

    #endregion
}
