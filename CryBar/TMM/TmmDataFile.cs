using System.Buffers.Binary;

namespace CryBar.TMM;

/// <summary>
/// Parses .tmm.data files containing vertex, index, skinning, and height buffers.
/// Uses offset/size pairs from the companion TmmFile header to locate each buffer.
/// </summary>
public class TmmDataFile
{
    public bool Parsed { get; private set; }

    public TmmVertex[]? Vertices { get; private set; }
    public ushort[]? Indices { get; private set; }
    public TmmSkinWeight[]? SkinWeights { get; private set; }
    public Half[]? Heights { get; private set; }
    public TmmDestructionVertex[]? DestructionVertices { get; private set; }
    public byte[]? VertexColors { get; private set; }
    public TmmSpeedTreeVertex[]? SpeedTreeVertices { get; private set; }

    readonly TmmFile _tmm;

    /// <param name="data">Raw .tmm.data file bytes.</param>
    /// <param name="tmm">Parsed companion TmmFile providing buffer layout.</param>
    public TmmDataFile(ReadOnlyMemory<byte> data, TmmFile tmm)
    {
        _tmm = tmm;
        if (!tmm.Parsed) { Parsed = false; return; }
        Parsed = Parse(data.Span);
    }

    bool Parse(ReadOnlySpan<byte> data)
    {
        var numVertices = _tmm.NumVertices;
        var numTriangleVerts = _tmm.NumTriangleVerts;
        bool hasSkinning = _tmm.NumBones > 0;

        // Vertex buffer
        if (_tmm.VerticesByteLength > 0)
        {
            var vertByteSize = (int)numVertices * TmmVertex.SizeInBytes;
            var vertStart = (int)_tmm.VerticesStart;
            if (vertStart + vertByteSize > data.Length) return false;

            var vertices = new TmmVertex[numVertices];
            var offset = vertStart;
            for (int i = 0; i < numVertices; i++)
            {
                vertices[i] = new TmmVertex
                {
                    PosX = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset, 2)),
                    PosY = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset + 2, 2)),
                    PosZ = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset + 4, 2)),
                    U = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset + 6, 2)),
                    V = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset + 8, 2)),
                    TbnX = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 10, 2)),
                    TbnY = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 12, 2)),
                    TbnZ = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 14, 2))
                };
                offset += TmmVertex.SizeInBytes;
            }
            Vertices = vertices;
        }
        else if (numVertices == 0)
        {
            Vertices = [];
        }

        // Index buffer
        if (_tmm.TrianglesByteLength > 0)
        {
            var indexByteSize = (int)numTriangleVerts * 2;
            var idxStart = (int)_tmm.TrianglesStart;
            if (idxStart + indexByteSize > data.Length) return false;

            var indices = new ushort[numTriangleVerts];
            var offset = idxStart;
            for (int i = 0; i < numTriangleVerts; i++)
            {
                indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
                offset += 2;
            }
            Indices = indices;
        }
        else if (numTriangleVerts == 0)
        {
            Indices = [];
        }

        // Skinning buffer
        if (hasSkinning && _tmm.WeightsByteLength > 0)
        {
            var skinByteSize = (int)numVertices * TmmSkinWeight.SizeInBytes;
            var skinStart = (int)_tmm.WeightsStart;
            if (skinStart + skinByteSize > data.Length) return false;

            var skinWeights = new TmmSkinWeight[numVertices];
            var offset = skinStart;
            for (int i = 0; i < numVertices; i++)
            {
                skinWeights[i] = new TmmSkinWeight
                {
                    Weight0 = data[offset],
                    Weight1 = data[offset + 1],
                    Weight2 = data[offset + 2],
                    Weight3 = data[offset + 3],
                    BoneIndex0 = data[offset + 4],
                    BoneIndex1 = data[offset + 5],
                    BoneIndex2 = data[offset + 6],
                    BoneIndex3 = data[offset + 7]
                };
                offset += TmmSkinWeight.SizeInBytes;
            }
            SkinWeights = skinWeights;
        }

        // Height buffer
        if (_tmm.HeightsByteLength > 0)
        {
            var heightByteSize = (int)numVertices * 2;
            var heightStart = (int)_tmm.HeightsStart;
            if (heightStart + heightByteSize <= data.Length)
            {
                var heights = new Half[numVertices];
                var offset = heightStart;
                for (int i = 0; i < numVertices; i++)
                {
                    heights[i] = BinaryPrimitives.ReadHalfLittleEndian(data.Slice(offset, 2));
                    offset += 2;
                }
                Heights = heights;
            }
        }

        // Destruction buffer
        if (_tmm.DestructionBufferByteLength > 0)
        {
            var destByteSize = (int)numVertices * TmmDestructionVertex.SizeInBytes;
            var destStart = (int)_tmm.DestructionBufferStart;
            if (destStart + destByteSize <= data.Length)
            {
                var destVerts = new TmmDestructionVertex[numVertices];
                var offset = destStart;
                for (int i = 0; i < numVertices; i++)
                {
                    var packed = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
                    destVerts[i] = new TmmDestructionVertex
                    {
                        BurntInteriorColor = (byte)(packed >> 10),
                        DestructionBoneIndex = (ushort)(packed & 0x3FF)
                    };
                    offset += TmmDestructionVertex.SizeInBytes;
                }
                DestructionVertices = destVerts;
            }
        }

        // Color buffer
        if (_tmm.ColorBufferByteLength > 0)
        {
            var colorByteSize = (int)numVertices * 4;
            var colorStart = (int)_tmm.ColorBufferStart;
            if (colorStart + colorByteSize <= data.Length)
            {
                VertexColors = data.Slice(colorStart, colorByteSize).ToArray();
            }
        }

        // SpeedTree buffer
        if (_tmm.SpeedTreeBufferByteLength > 0)
        {
            var stByteSize = (int)numVertices * TmmSpeedTreeVertex.SizeInBytes;
            var stStart = (int)_tmm.SpeedTreeBufferStart;
            if (stStart + stByteSize <= data.Length)
            {
                var stVerts = new TmmSpeedTreeVertex[numVertices];
                var offset = stStart;
                for (int i = 0; i < numVertices; i++)
                {
                    stVerts[i] = new TmmSpeedTreeVertex
                    {
                        AnchorX = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)),
                        AnchorY = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2, 2)),
                        AnchorZ = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 4, 2)),
                        GeometryType = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 6, 2)),
                        WindDataX = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 8, 2)),
                        WindDataY = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 10, 2))
                    };
                    offset += TmmSpeedTreeVertex.SizeInBytes;
                }
                SpeedTreeVertices = stVerts;
            }
        }

        return Vertices != null && Indices != null;
    }

    /// <summary>
    /// Generates a human-readable summary of the data file contents.
    /// </summary>
    public string GetSummary()
    {
        if (!Parsed) return "(TMM data not parsed)";

        var lines = new List<string>
        {
            $"TMM Data File",
            $"Vertices: {Vertices?.Length ?? 0}",
            $"Indices: {Indices?.Length ?? 0} ({(Indices?.Length ?? 0) / 3} triangles)"
        };

        if (SkinWeights != null)
            lines.Add($"Skin weights: {SkinWeights.Length} entries");

        if (Heights != null)
            lines.Add($"Height values: {Heights.Length} entries");

        if (DestructionVertices != null)
            lines.Add($"Destruction vertices: {DestructionVertices.Length} entries");

        if (VertexColors != null)
            lines.Add($"Vertex colors: {VertexColors.Length / 4} entries");

        if (SpeedTreeVertices != null)
            lines.Add($"SpeedTree vertices: {SpeedTreeVertices.Length} entries");

        return string.Join("\n", lines);
    }
}
