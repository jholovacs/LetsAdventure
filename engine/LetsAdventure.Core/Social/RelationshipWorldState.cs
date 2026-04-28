namespace LetsAdventure.Core.Social;

/// <summary>
/// Directed personal reputation: how <c>observerId</c> feels about <c>targetId</c> before faction blending.
/// Persist with save games; quest scripts adjust via <see cref="AddPersonalDelta"/>.
/// </summary>
public sealed class RelationshipWorldState
{
    private static string PairKey(string observerId, string targetId) =>
        string.Concat(observerId, "\x1e", targetId);

    private readonly Dictionary<string, double> _personal = new(StringComparer.Ordinal);

    public double GetPersonal(string observerId, string targetId) =>
        _personal.GetValueOrDefault(PairKey(observerId, targetId), 0);

    public void SetPersonal(string observerId, string targetId, double value)
    {
        _personal[PairKey(observerId, targetId)] = value;
    }

    public void AddPersonalDelta(string observerId, string targetId, double delta)
    {
        var k = PairKey(observerId, targetId);
        _personal[k] = _personal.GetValueOrDefault(k, 0) + delta;
    }

    public RelationshipWorldState Clone()
    {
        var c = new RelationshipWorldState();
        foreach (var kv in _personal)
            c._personal[kv.Key] = kv.Value;
        return c;
    }

    public static RelationshipWorldState FromSeeds(IEnumerable<RelationshipSeed> seeds)
    {
        var s = new RelationshipWorldState();
        foreach (var x in seeds)
            s.SetPersonal(x.ObserverId, x.TargetId, x.Value);
        return s;
    }
}

public sealed class RelationshipSeed
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double Value { get; set; }
}
