using System.Collections.Generic;

namespace CryBar.Scenario.Editor.Commands;

public readonly record struct PlayerFields(
    string Name, uint God, uint StartAge, int PopLimit, int InitPopCap,
    float Gold, float Wood, float Food, float Favor, float ResTotal)
{
    public static PlayerFields From(ScenarioPlayer p) => new(
        p.Name, p.God, p.StartAge, p.PopLimit, p.InitPopCap,
        p.Gold, p.Wood, p.Food, p.Favor, p.ResTotal);

    public void ApplyTo(ScenarioPlayer p)
    {
        p.Name = Name;
        p.God = God;
        p.StartAge = StartAge;
        p.PopLimit = PopLimit;
        p.InitPopCap = InitPopCap;
        p.Gold = Gold;
        p.Wood = Wood;
        p.Food = Food;
        p.Favor = Favor;
        p.ResTotal = ResTotal;
    }
}

public sealed class SetPlayerFields : IScenarioCommand
{
    readonly ScenarioPlayer _player;
    readonly PlayerFields _old;
    readonly PlayerFields _new;

    SetPlayerFields(ScenarioPlayer player, PlayerFields oldFields, PlayerFields newFields)
    { _player = player; _old = oldFields; _new = newFields; }

    public string DisplayName => "Edit player";
    public RenderHint Hint => RenderHint.Players;

    public static SetPlayerFields? Create(ScenarioPlayer player, PlayerFields newFields)
    {
        var old = PlayerFields.From(player);
        if (old == newFields) return null;
        return new SetPlayerFields(player, old, newFields);
    }

    public void Apply(ScenarioTerrain _, List<ScenarioEntity> __) => _new.ApplyTo(_player);
    public void Undo(ScenarioTerrain _, List<ScenarioEntity> __) => _old.ApplyTo(_player);
}
