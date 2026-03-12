namespace CryBar.TMM;

public readonly struct TmmMeshGroup
{
    public uint VertexStart { get; init; }
    public uint IndexStart { get; init; }
    public uint VertexCount { get; init; }
    public uint IndexCount { get; init; }
    public uint MaterialIndex { get; init; }
    public uint ShaderIndex { get; init; }

    public uint TriangleCount => IndexCount / 3;
}
