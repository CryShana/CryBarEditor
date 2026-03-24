namespace CryBar.TMM;

public sealed class TmmPhysicsTemplate
{
    public int ShapeType { get; init; }
    public int MaxVertices { get; init; }
    public int EffectType { get; init; }
    public bool IsFixedMotion { get; init; }
    public bool IsPhysicsControlled { get; init; }
    public float Restitution { get; init; }
    public float Density { get; init; }
    public float PenetrationDepth { get; init; }
    public required float[][] HullPoints { get; init; }
}
