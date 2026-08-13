using System.Collections.Generic;

namespace CryBar.Scenario.Editor.Commands;

public sealed class SetPlayerDiplomacy : IScenarioCommand
{
    readonly ScenarioPlayer _player;
    readonly int _slot;
    readonly int _old;
    readonly int _new;

    SetPlayerDiplomacy(ScenarioPlayer player, int slot, int oldStance, int newStance)
    { _player = player; _slot = slot; _old = oldStance; _new = newStance; }

    public string DisplayName => "Set diplomacy";
    public RenderHint Hint => RenderHint.Players;

    public static SetPlayerDiplomacy? Create(ScenarioPlayer player, int slot, int newStance)
    {
        if (slot < 0 || slot >= player.Diplomacy.Count) return null;
        var old = player.Diplomacy[slot];
        if (old == newStance) return null;
        return new SetPlayerDiplomacy(player, slot, old, newStance);
    }

    public void Apply(ScenarioTerrain _, List<ScenarioEntity> __) => _player.Diplomacy[_slot] = _new;
    public void Undo(ScenarioTerrain _, List<ScenarioEntity> __) => _player.Diplomacy[_slot] = _old;
}
