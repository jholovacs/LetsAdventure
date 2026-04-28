using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.Npcs;

/// <summary>
/// Updates needs, commutes, and activity choice for every registered NPC.
/// </summary>
public sealed class NpcDailyLifeSimulator
{
    private readonly IReadOnlyDictionary<string, Vec3> _anchors;
    private readonly IReadOnlyDictionary<string, Establishment> _establishments;

    public NpcDailyLifeSimulator(
        IReadOnlyDictionary<string, Establishment> establishments,
        IReadOnlyDictionary<string, Vec3> anchors)
    {
        _establishments = establishments;
        _anchors = anchors;
    }

    public void Tick(
        NpcWorldState world,
        IReadOnlyDictionary<string, NpcInstanceProfile> instances,
        IReadOnlyDictionary<string, NpcTemplateData> templates,
        double elapsedMinutes)
    {
        if (elapsedMinutes <= 0) return;

        world.Clock.AdvanceMinutes(elapsedMinutes);

        foreach (var (npcId, agent) in world.Agents)
        {
            if (!instances.TryGetValue(npcId, out var profile) ||
                !templates.TryGetValue(profile.TemplateId, out var template))
                continue;

            agent.MinutesSinceLastDecision += elapsedMinutes;

            if (agent.CurrentActivity == NpcActivityKind.Commute)
            {
                TickCommute(agent, elapsedMinutes);
                continue;
            }

            ApplyPassiveNeedDrift(agent, template, elapsedMinutes, world.Clock);
            ApplyActivityProgress(agent, template, elapsedMinutes, world.Clock);

            var urgent = NeedsInterrupt(agent, template, world.Clock);
            var shouldDecide = urgent || agent.MinutesSinceLastDecision >= NpcLifeConstants.MinDecisionIntervalMinutes;

            if (shouldDecide)
            {
                DecideNextAction(world, agent, profile, template);
                agent.MinutesSinceLastDecision = 0;
            }
        }
    }

    private void TickCommute(NpcAgentState agent, double dt)
    {
        agent.CommuteRemainingMinutes -= dt;
        if (agent.CommuteRemainingMinutes > 0) return;

        agent.CommuteRemainingMinutes = 0;
        if (agent.CommuteTargetEstablishmentId is { } dest &&
            _establishments.ContainsKey(dest))
        {
            agent.CurrentEstablishmentId = dest;
            agent.CurrentActivity = NpcActivityKind.Idle;
        }

        agent.CommuteTargetEstablishmentId = null;
    }

    private static void ApplyPassiveNeedDrift(
        NpcAgentState agent,
        NpcTemplateData template,
        double dt,
        WorldClock clock)
    {
        var h = (double)dt / 60;
        var intensity = template.NeedIntensity;

        if (agent.CurrentActivity == NpcActivityKind.Sleep)
        {
            var n = agent.Needs;
            n.Fatigue += template.FatiguePerHourSleeping * h * intensity;
            n.Hunger += template.HungerPerHour * 0.35 * h * intensity;
            n.Social += template.SocialPerHour * 0.2 * h * intensity;
            n.Recreation += template.RecreationPerHour * 0.15 * h * intensity;
            n.Clamp();
            agent.Needs = n;
            return;
        }

        {
            var n = agent.Needs;
            n.Hunger += template.HungerPerHour * h * intensity;
            n.Fatigue += template.FatiguePerHourAwake * h * intensity;
            n.Social += template.SocialPerHour * h * intensity;
            n.Recreation += template.RecreationPerHour * h * intensity;
            n.Clamp();
            agent.Needs = n;
        }

        if (InWorkWindow(template, clock) && agent.CurrentActivity != NpcActivityKind.Work)
        {
            agent.WorkObligation = Math.Min(100, agent.WorkObligation + template.WorkObligationPerHour * h);
        }
    }

    private void ApplyActivityProgress(
        NpcAgentState agent,
        NpcTemplateData template,
        double dt,
        WorldClock clock)
    {
        agent.MinutesInCurrentActivity += dt;

        switch (agent.CurrentActivity)
        {
            case NpcActivityKind.Eat:
                {
                    var n = agent.Needs;
                    n.Hunger -= NpcLifeConstants.EatHungerPerMinute * dt;
                    n.Clamp();
                    agent.Needs = n;
                    if (n.Hunger <= NpcLifeConstants.EatSatisfiedBelow)
                    {
                        agent.CurrentActivity = NpcActivityKind.Idle;
                        agent.MinutesInCurrentActivity = 0;
                    }

                    break;
                }
            case NpcActivityKind.Socialize:
                {
                    var n = agent.Needs;
                    n.Social -= NpcLifeConstants.SocializeSocialPerMinute * dt;
                    n.Clamp();
                    agent.Needs = n;
                    if (n.Social <= 12)
                    {
                        agent.CurrentActivity = NpcActivityKind.Idle;
                        agent.MinutesInCurrentActivity = 0;
                    }

                    break;
                }
            case NpcActivityKind.Recreation:
            case NpcActivityKind.Train:
                {
                    var n = agent.Needs;
                    n.Recreation -= NpcLifeConstants.RecreationFunPerMinute * dt;
                    n.Clamp();
                    agent.Needs = n;
                    if (n.Recreation <= 12)
                    {
                        agent.CurrentActivity = NpcActivityKind.Idle;
                        agent.MinutesInCurrentActivity = 0;
                    }

                    break;
                }
            case NpcActivityKind.Work:
                {
                    agent.WorkObligation = Math.Max(0, agent.WorkObligation - NpcLifeConstants.WorkObligationReliefPerMinute * dt);
                    break;
                }
            case NpcActivityKind.Worship:
                {
                    var n = agent.Needs;
                    n.Social -= NpcLifeConstants.SocializeSocialPerMinute * 0.4 * dt;
                    n.Recreation -= NpcLifeConstants.RecreationFunPerMinute * 0.3 * dt;
                    n.Clamp();
                    agent.Needs = n;
                    break;
                }
            case NpcActivityKind.Sleep:
                if (!InSleepWindow(template, clock.MinuteOfDay))
                {
                    agent.CurrentActivity = NpcActivityKind.Idle;
                    agent.MinutesInCurrentActivity = 0;
                }

                break;
        }
    }

    private bool NeedsInterrupt(NpcAgentState agent, NpcTemplateData template, WorldClock clock)
    {
        if (agent.CurrentActivity == NpcActivityKind.Commute) return false;

        if (agent.Needs.Hunger >= NpcLifeConstants.HungerUrgent && agent.CurrentActivity != NpcActivityKind.Eat)
            return true;

        if (agent.Needs.Fatigue >= NpcLifeConstants.FatigueUrgent && agent.CurrentActivity != NpcActivityKind.Sleep)
            return true;

        if (InSleepWindow(template, clock.MinuteOfDay) && agent.CurrentActivity != NpcActivityKind.Sleep)
        {
            if (agent.Needs.Fatigue > 40 || clock.CurrentTimeBand == TimeBand.Night)
                return true;
        }

        return false;
    }

    private void DecideNextAction(
        NpcWorldState world,
        NpcAgentState agent,
        NpcInstanceProfile profile,
        NpcTemplateData template)
    {
        var est = GetEstablishment(agent.CurrentEstablishmentId);
        var clock = world.Clock;

        // Forced priorities
        if (agent.Needs.Hunger >= NpcLifeConstants.HungerUrgent || (agent.Needs.Hunger > 55 && CanEatHere(est)))
        {
            if (CanEatHere(est))
            {
                StartActivity(agent, NpcActivityKind.Eat);
                return;
            }

            if (TryStartCommute(agent, FindBestFoodPlace(profile, template)))
                return;
        }

        if ((agent.Needs.Fatigue >= NpcLifeConstants.FatigueUrgent || ShouldSleep(template, clock, agent)) && agent.CurrentActivity != NpcActivityKind.Sleep)
        {
            var sleepTarget = profile.HomeEstablishmentId;
            if (!IsSleepPlace(GetEstablishment(sleepTarget)))
                sleepTarget = FindInn()?.Id ?? sleepTarget;

            if (agent.CurrentEstablishmentId == sleepTarget && IsSleepPlace(est))
            {
                StartActivity(agent, NpcActivityKind.Sleep);
                return;
            }

            if (TryStartCommute(agent, sleepTarget))
                return;
        }

        if (InWorkWindow(template, clock) && agent.WorkObligation > 25)
        {
            if (agent.CurrentEstablishmentId == profile.WorkEstablishmentId)
            {
                StartActivity(agent, NpcActivityKind.Work);
                return;
            }

            if (TryStartCommute(agent, profile.WorkEstablishmentId))
                return;
        }

        // Utility choice among social / recreation / worship / train / idle
        var best = PickDiscretionaryActivity(agent, profile, template, est, clock);
        if (best.activity == NpcActivityKind.Idle)
        {
            agent.CurrentActivity = NpcActivityKind.Idle;
            return;
        }

        if (best.targetEstablishmentId != null &&
            best.targetEstablishmentId != agent.CurrentEstablishmentId)
        {
            if (TryStartCommute(agent, best.targetEstablishmentId))
                return;
        }

        StartActivity(agent, best.activity);
    }

    private (NpcActivityKind activity, string? targetEstablishmentId) PickDiscretionaryActivity(
        NpcAgentState agent,
        NpcInstanceProfile profile,
        NpcTemplateData template,
        Establishment? here,
        WorldClock clock)
    {
        var candidates = new List<(NpcActivityKind act, string? dest, double score)>
        {
            (NpcActivityKind.Idle, null, ScoreIdle(agent, template, clock)),
        };

        if (agent.Needs.Social > 35)
            candidates.Add((NpcActivityKind.Socialize, FindSocialPlace()?.Id, agent.Needs.Social * Bias(template, "socialize")));

        if (agent.Needs.Recreation > 30)
            candidates.Add((NpcActivityKind.Recreation, FindRecreationPlace(template)?.Id, agent.Needs.Recreation * Bias(template, "recreation")));

        if (template.ActivityUtilityBias.TryGetValue("worship", out var w) && w > 1.05 && clock.CurrentTimeBand is TimeBand.Dawn or TimeBand.Dusk)
            candidates.Add((NpcActivityKind.Worship, FindTemple()?.Id, 40 * w));

        if (template.ActivityUtilityBias.TryGetValue("train", out var t) && t > 1.05)
            candidates.Add((NpcActivityKind.Train, FindTrainingYard()?.Id, agent.Needs.Recreation * 0.6 * t));

        if (here != null && KindAllowsWork(here.Kind) && agent.CurrentEstablishmentId == profile.WorkEstablishmentId && InWorkWindow(template, clock))
            candidates.Add((NpcActivityKind.Work, profile.WorkEstablishmentId, agent.WorkObligation * template.WorkEthic * Bias(template, "work")));

        var best = candidates.MaxBy(x => x.score);
        return (best.act, best.dest);
    }

    private static double ScoreIdle(NpcAgentState agent, NpcTemplateData template, WorldClock clock)
    {
        var s = 15.0;
        if (!InWorkWindow(template, clock)) s += 10;
        if (agent.Needs.Hunger < 40 && agent.Needs.Fatigue < 50) s += 8;
        return s * Bias(template, "idle");
    }

    private static double Bias(NpcTemplateData t, string key) =>
        t.ActivityUtilityBias.TryGetValue(key, out var b) ? b : 1;

    private bool ShouldSleep(NpcTemplateData template, WorldClock clock, NpcAgentState agent) =>
        InSleepWindow(template, clock.MinuteOfDay) && agent.Needs.Fatigue > 55;

    private static bool InWorkWindow(NpcTemplateData t, WorldClock clock) =>
        MinuteInWindow(clock.MinuteOfDay, t.WorkStartMinute, t.WorkEndMinute);

    private static bool InSleepWindow(NpcTemplateData t, double minuteOfDay) =>
        WrapSleepWindow(t.SleepStartMinute, t.SleepEndMinute, minuteOfDay);

    private static bool WrapSleepWindow(int sleepStart, int sleepEnd, double m)
    {
        if (sleepStart < sleepEnd)
            return m >= sleepStart && m < sleepEnd;

        return m >= sleepStart || m < sleepEnd;
    }

    private static bool MinuteInWindow(double m, int start, int end)
    {
        if (start <= end)
            return m >= start && m < end;
        return m >= start || m < end;
    }

    private static bool KindAllowsWork(EstablishmentKind k) =>
        k is EstablishmentKind.Workplace
            or EstablishmentKind.Temple
            or EstablishmentKind.Market
            or EstablishmentKind.TrainingYard;

    private static bool CanEatHere(Establishment? e) =>
        e != null && (e.Kind is EstablishmentKind.FoodVendor or EstablishmentKind.Market or EstablishmentKind.Inn or EstablishmentKind.Teahouse
            || e.Tags.Contains("food", StringComparer.Ordinal));

    private static bool IsSleepPlace(Establishment? e) =>
        e != null && (e.Kind is EstablishmentKind.Home or EstablishmentKind.Inn || e.Tags.Contains("sleep", StringComparer.Ordinal));

    private Establishment? GetEstablishment(string id) =>
        string.IsNullOrEmpty(id) ? null : _establishments.GetValueOrDefault(id);

    private void StartActivity(NpcAgentState agent, NpcActivityKind kind)
    {
        if (agent.CurrentActivity != kind)
            agent.MinutesInCurrentActivity = 0;
        agent.CurrentActivity = kind;
    }

    private bool TryStartCommute(NpcAgentState agent, string? targetEstablishmentId)
    {
        if (string.IsNullOrEmpty(targetEstablishmentId) || targetEstablishmentId == agent.CurrentEstablishmentId)
            return false;

        if (!_establishments.TryGetValue(targetEstablishmentId, out var dest))
            return false;

        var from = GetEstablishment(agent.CurrentEstablishmentId);
        var minutes = CommuteMinutes(from, dest);

        agent.CurrentActivity = NpcActivityKind.Commute;
        agent.CommuteTargetEstablishmentId = targetEstablishmentId;
        agent.CommuteRemainingMinutes = minutes;
        return true;
    }

    private double CommuteMinutes(Establishment? from, Establishment to)
    {
        if (from == null || !_anchors.TryGetValue(from.AnchorId, out var a) || !_anchors.TryGetValue(to.AnchorId, out var b))
            return NpcLifeConstants.DefaultCommuteMinutes;

        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        var dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return Math.Max(2, NpcLifeConstants.DefaultCommuteMinutes + dist * NpcLifeConstants.CommuteMinutesPerUnitDistance);
    }

    private string? FindBestFoodPlace(NpcInstanceProfile profile, NpcTemplateData template)
    {
        Establishment? best = null;
        var bestScore = -1d;
        foreach (var e in _establishments.Values)
        {
            if (!CanEatHere(e)) continue;
            var score = 40.0;
            if (template.PreferredFoodKinds.Contains(e.Kind)) score += 25;
            if (e.Id == profile.HomeEstablishmentId) score += 8;
            if (score > bestScore)
            {
                bestScore = score;
                best = e;
            }
        }

        return best?.Id;
    }

    private Establishment? FindInn() =>
        _establishments.Values.FirstOrDefault(e => e.Kind == EstablishmentKind.Inn);

    private Establishment? FindSocialPlace() =>
        _establishments.Values.FirstOrDefault(e =>
            e.Kind is EstablishmentKind.Teahouse or EstablishmentKind.SocialHall or EstablishmentKind.Market);

    private Establishment? FindRecreationPlace(NpcTemplateData template)
    {
        foreach (var k in template.PreferredRecreationKinds)
        {
            var hit = _establishments.Values.FirstOrDefault(e => e.Kind == k);
            if (hit != null) return hit;
        }

        return _establishments.Values.FirstOrDefault(e => e.Kind == EstablishmentKind.Teahouse);
    }

    private Establishment? FindTemple() =>
        _establishments.Values.FirstOrDefault(e => e.Kind == EstablishmentKind.Temple);

    private Establishment? FindTrainingYard() =>
        _establishments.Values.FirstOrDefault(e => e.Kind == EstablishmentKind.TrainingYard);
}
