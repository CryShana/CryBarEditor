namespace CryBar.Scenario.Editor;

[System.Flags]
public enum RenderHint
{
    None             = 0,
    TerrainTexture   = 1 << 0,
    TerrainWater     = 1 << 1,
    TerrainGeometry  = 1 << 2,
    EntityList       = 1 << 3,
    EntityField      = 1 << 4,
    Players          = 1 << 5,
}
