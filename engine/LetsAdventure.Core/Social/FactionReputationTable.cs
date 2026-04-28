namespace LetsAdventure.Core.Social;

/// <summary>
/// Directed standing: how <c>fromFaction</c> views <c>toFaction</c> (-100 hostile … +100 allied).
/// Missing pairs default to 0 (neutral).
/// </summary>
public sealed class FactionReputationTable
{
    private static string Key(string fromFactionId, string toFactionId) =>
        string.Concat(fromFactionId, "\x1e", toFactionId);

    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);

    public void Set(string fromFactionId, string toFactionId, double value)
    {
        _values[Key(fromFactionId, toFactionId)] = value;
    }

    public double Get(string fromFactionId, string toFactionId) =>
        _values.GetValueOrDefault(Key(fromFactionId, toFactionId), 0);

    public static FactionReputationTable FromPairs(IEnumerable<FactionReputationPair> pairs)
    {
        var t = new FactionReputationTable();
        foreach (var p in pairs)
            t.Set(p.FromFactionId, p.ToFactionId, p.Value);
        return t;
    }
}

public sealed class FactionReputationPair
{
    public string FromFactionId { get; set; } = "";
    public string ToFactionId { get; set; } = "";
    public double Value { get; set; }
}
