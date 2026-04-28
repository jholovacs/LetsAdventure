namespace LetsAdventure.Core.Simulation;

/// <summary>
/// Authoritative simulation clock for NPC routines and world systems.
/// </summary>
public sealed class WorldClock
{
    /// <summary>0-based day index (campaign start).</summary>
    public int CalendarDay { get; set; }

    /// <summary>Minutes since local midnight [0, 1440).</summary>
    public double MinuteOfDay { get; set; }

    public void AdvanceMinutes(double delta)
    {
        MinuteOfDay += delta;
        while (MinuteOfDay >= MinutesPerDay)
        {
            MinuteOfDay -= MinutesPerDay;
            CalendarDay++;
        }
    }

    public TimeBand CurrentTimeBand => MinuteOfDay switch
    {
        >= 300 and < 480 => TimeBand.Dawn,
        >= 480 and < 1020 => TimeBand.Day,
        >= 1020 and < 1200 => TimeBand.Dusk,
        _ => TimeBand.Night,
    };

    public const double MinutesPerDay = 1440;
}
