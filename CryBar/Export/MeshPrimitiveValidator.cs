namespace CryBar.Export;

// Returns null when valid, otherwise a message fragment for the caller to wrap in its own exception type.
internal static class MeshPrimitiveValidator
{
    // tangents may be null when they are generated after validation.
    internal static string? CheckLengths(float[] positions, float[] normals, float[]? tangents, float[] texCoords,
        byte[]? joints, float[]? weights, int indexCount)
    {
        if (positions.Length % 3 != 0)
            return $"POSITION has {positions.Length} values, not a multiple of 3";

        int vc = positions.Length / 3;
        return CheckLength("NORMAL", normals.Length, 3, vc)
            ?? CheckLength("TANGENT", tangents?.Length, 4, vc)
            ?? CheckLength("TEXCOORD_0", texCoords.Length, 2, vc)
            ?? CheckLength("JOINTS_0", joints?.Length, 4, vc)
            ?? CheckLength("WEIGHTS_0", weights?.Length, 4, vc)
            ?? (indexCount % 3 != 0 ? $"index count {indexCount} is not a multiple of 3 (triangle lists only)" : null);
    }

    internal static string? CheckIndexRange(uint[] indices, int vertexCount)
    {
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= (uint)vertexCount)
                return $"index {indices[i]} at position {i} is out of range for {vertexCount} vertices";
        }
        return null;
    }

    static string? CheckLength(string attr, int? length, int components, int vertexCount) =>
        length is { } len && len != components * vertexCount
            ? $"{attr} has {len} values, expected {components * vertexCount} for {vertexCount} POSITION vertices"
            : null;
}
