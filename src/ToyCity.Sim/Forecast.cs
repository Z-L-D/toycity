namespace ToyCity.Sim;

/// <summary>The city at the end of one forecast month.</summary>
public sealed record ForecastPoint(int Year, int Month, int Population, int Jobs, int Workers, long Funds, int[] Demand, int PowerPercent, int WaterPercent, int Abandoned);

/// <summary>Something that happens during a forecast, in words, with the month it happens.</summary>
public sealed record ForecastEvent(int Year, int Month, string Severity, string Text, int X, int Y);

public sealed record ForecastReport(int Months, List<ForecastPoint> Points, List<ForecastEvent> Events, List<string> Summary);

public sealed partial class World
{
    /// <summary>
    /// Runs a copy of the city ahead and reports what happens. The sim is deterministic, so with no
    /// <paramref name="plan"/> this is exactly what will happen if the player does nothing. A plan is a list of
    /// actions applied first: "what if I build this?"
    /// </summary>
    public ForecastReport Forecast(int months, IReadOnlyList<SimAction>? plan = null)
    {
        months = Math.Clamp(months, 1, 120);
        using var copy = FromSnapshot(Content, Snapshot());
        return copy.RunForecast(months, plan);
    }

    /// <summary>Runs this world forward for a forecast. Call it on a copy: it changes the world.</summary>
    public ForecastReport RunForecast(int months, IReadOnlyList<SimAction>? plan)
    {
        var points = new List<ForecastPoint>();
        var events = new List<ForecastEvent>();
        int pop0 = Population;
        long funds0 = Funds;
        if (plan != null)
            foreach (var a in plan) Enqueue(a);
        ApplyPending();
        Notices.Clear();

        var known = new HashSet<string>(Alerts().Select(a => a.Key));
        long firstRed = -1;
        int peakAbandoned = 0;
        for (int m = 0; m < months; m++)
        {
            Run(DaysPerMonth);
            foreach (var n in Notices) events.Add(new ForecastEvent(Year, Month, n.Severity, n.Text, n.X, n.Y));
            Notices.Clear();
            // New standing problems are the useful warnings: a brownout starting in month 5, say.
            var alerts = Alerts();
            foreach (var a in alerts)
                if (known.Add(a.Key) && a.Severity != "info") events.Add(new ForecastEvent(Year, Month, a.Severity, a.Text, a.X, a.Y));
            known.IntersectWith(alerts.Select(a => a.Key));
            if (Funds < 0 && firstRed < 0) firstRed = m + 1;
            int abandoned = Buildings.Count(b => b is { Abandoned: true });
            peakAbandoned = Math.Max(peakAbandoned, abandoned);
            points.Add(new ForecastPoint(Year, Month, Population, Jobs, Workers, Funds, (int[])Demand.Clone(),
                PowerCapacity > 0 ? (int)(PowerDemand * 100 / PowerCapacity) : 0,
                WaterCapacity > 0 ? (int)(WaterDemand * 100 / WaterCapacity) : 0, abandoned));
        }

        var summary = new List<string>();
        var last = points[^1];
        int dp = last.Population - pop0;
        summary.Add(dp == 0 ? $"Population holds at {pop0:N0}."
            : $"Population {(dp > 0 ? "grows" : "falls")} from {pop0:N0} to {last.Population:N0} ({dp:+#,0;-#,0}).");
        long df = last.Funds - funds0;
        summary.Add($"The treasury goes from ${funds0:N0} to ${last.Funds:N0} ({(df >= 0 ? "+" : "-")}${Math.Abs(df):N0}).");
        if (firstRed > 0) summary.Add($"Money runs out in month {firstRed}.");
        int powerMonth = points.FindIndex(p => p.PowerPercent > 100);
        if (powerMonth >= 0) summary.Add($"Power runs short in month {powerMonth + 1}.");
        else if (last.PowerPercent > 75) summary.Add($"Power use reaches {last.PowerPercent}% of capacity.");
        int waterMonth = points.FindIndex(p => p.WaterPercent > 100);
        if (waterMonth >= 0) summary.Add($"Water runs short in month {waterMonth + 1}.");
        if (peakAbandoned > 0) summary.Add($"Up to {peakAbandoned} buildings stand abandoned.");
        return new ForecastReport(months, points, events, summary);
    }
}
