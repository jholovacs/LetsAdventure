namespace LetsAdventure.Core.Social;

/// <summary>
/// Directed friendship and romantic interest between characters (separate from faction disposition).
/// </summary>
public sealed class IntimacyWorldState
{
    private static string Key(string a, string b) => string.Concat(a, "\x1e", b);

    private readonly Dictionary<string, double> _friendship = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _romantic = new(StringComparer.Ordinal);

    public double GetFriendship(string observerId, string targetId) =>
        _friendship.GetValueOrDefault(Key(observerId, targetId), 0);

    public double GetRomanticInterest(string observerId, string targetId) =>
        _romantic.GetValueOrDefault(Key(observerId, targetId), 0);

    public void SetFriendship(string observerId, string targetId, double value)
    {
        _friendship[Key(observerId, targetId)] = Math.Clamp(value, -100, 100);
    }

    public void AddFriendshipDelta(string observerId, string targetId, double delta)
    {
        var k = Key(observerId, targetId);
        _friendship[k] = Math.Clamp(_friendship.GetValueOrDefault(k, 0) + delta, -100, 100);
    }

    public void SetRomanticInterest(string observerId, string targetId, double value)
    {
        _romantic[Key(observerId, targetId)] = Math.Clamp(value, 0, 100);
    }

    public void AddRomanticInterestDelta(string observerId, string targetId, double delta)
    {
        var k = Key(observerId, targetId);
        _romantic[k] = Math.Clamp(_romantic.GetValueOrDefault(k, 0) + delta, 0, 100);
    }

    public IntimacyWorldState Clone()
    {
        var c = new IntimacyWorldState();
        foreach (var kv in _friendship) c._friendship[kv.Key] = kv.Value;
        foreach (var kv in _romantic) c._romantic[kv.Key] = kv.Value;
        return c;
    }

    public static IntimacyWorldState FromSeeds(IEnumerable<IntimacyFriendshipSeed> friendship, IEnumerable<IntimacyRomanticSeed> romantic)
    {
        var w = new IntimacyWorldState();
        foreach (var s in friendship)
            w.SetFriendship(s.ObserverId, s.TargetId, s.Value);
        foreach (var s in romantic)
            w.SetRomanticInterest(s.ObserverId, s.TargetId, s.Value);
        return w;
    }
}

public sealed class IntimacyFriendshipSeed
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double Value { get; set; }
}

public sealed class IntimacyRomanticSeed
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double Value { get; set; }
}
