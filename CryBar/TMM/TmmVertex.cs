namespace CryBar.TMM;

public readonly struct TmmVertex
{
    /// <summary>Position X (half-precision float).</summary>
    public Half PosX { get; init; }
    /// <summary>Position Y (half-precision float).</summary>
    public Half PosY { get; init; }
    /// <summary>Position Z (half-precision float).</summary>
    public Half PosZ { get; init; }

    /// <summary>Texture coordinate U (half-precision float).</summary>
    public Half U { get; init; }
    /// <summary>Texture coordinate V (half-precision float).</summary>
    public Half V { get; init; }

    /// <summary>Packed TBN quaternion X component (u16).</summary>
    public ushort TbnX { get; init; }
    /// <summary>Packed TBN quaternion Y component (u16).</summary>
    public ushort TbnY { get; init; }
    /// <summary>Packed TBN quaternion Z component (u16).</summary>
    public ushort TbnZ { get; init; }

    /// <summary>Size of a single vertex in the .tmm.data file (bytes).</summary>
    public const int SizeInBytes = 16; // 3×2(pos) + 2×2(uv) + 3×2(tbn) = 16
}
