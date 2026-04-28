namespace LetsAdventure.Core.Social;

/// <summary>
/// Tunables for 面子 / public dignity: how characters weight reputation and when they count as "high face"
/// (more willing to take extreme measures to avoid or repair shame).
/// </summary>
public sealed class FaceCultureRules
{
    public double MinFace { get; set; } = 0;
    public double MaxFace { get; set; } = 100;

    /// <summary>When <see cref="NpcInstanceProfile.Face"/> or player social face is omitted.</summary>
    public double DefaultFace { get; set; } = 50;

    /// <summary>
    /// At or above this value, characters are treated as highly face-conscious for events and AI hooks
    /// (<c>high_face</c> condition).
    /// </summary>
    public double HighFaceThreshold { get; set; } = 72;

    public static FaceCultureRules Default { get; } = new();
}
