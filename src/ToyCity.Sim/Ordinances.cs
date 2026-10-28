using System.Text.Json;

namespace ToyCity.Sim;

public enum OrdinanceAvailability : byte { Always = 0, Unlock = 1, Random = 2 }

/// <summary>A built-in ordinance from content/ordinances.json. All of its behaviour is its rule graph.</summary>
public sealed class OrdinanceDef
{
    public string Id = "";
    public string Name = "";
    public string Category = "";
    public string Description = "";
    public OrdinanceAvailability Kind;
    public int MinPopulation;
    public RuleGraph? Unlock;
    public string UnlockText = "";
    public int ChancePerYear;
    public int OfferMonths;
    public int DurationMonths;
    public RuleGraph Graph = new();
    public int Index;

    public static List<OrdinanceDef> Parse(string json)
    {
        var list = new List<OrdinanceDef>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var ids = new HashSet<string>();
        foreach (var o in doc.RootElement.GetProperty("ordinances").EnumerateArray())
        {
            var d = new OrdinanceDef
            {
                Id = o.GetProperty("id").GetString()!,
                Name = o.GetProperty("name").GetString()!,
                Category = o.TryGetProperty("category", out var c) ? c.GetString()! : "Other",
                Description = o.TryGetProperty("description", out var ds) ? ds.GetString()! : "",
                Index = list.Count,
            };
            if (!ids.Add(d.Id)) throw new InvalidDataException($"duplicate ordinance {d.Id}");
            var a = o.GetProperty("availability");
            d.Kind = a.GetProperty("kind").GetString() switch
            {
                "always" => OrdinanceAvailability.Always,
                "unlock" => OrdinanceAvailability.Unlock,
                "random" => OrdinanceAvailability.Random,
                var k => throw new InvalidDataException($"{d.Id}: unknown availability '{k}'"),
            };
            int Int(string name) => a.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
            d.MinPopulation = Int("minPopulation");
            d.ChancePerYear = Int("chancePerYear");
            d.OfferMonths = Math.Max(1, Int("offerMonths"));
            d.DurationMonths = Int("durationMonths");
            if (a.TryGetProperty("unlockText", out var ut)) d.UnlockText = ut.GetString()!;
            if (d.Kind == OrdinanceAvailability.Unlock)
                d.Unlock = RuleGraph.Compile(a.GetProperty("unlock"), d.Id + " unlock");
            d.Graph = RuleGraph.Compile(o.GetProperty("nodes"), d.Id);
            list.Add(d);
        }
        return list;
    }

    /// <summary>When the player can enact it, in words.</summary>
    public string AvailabilityText => Kind switch
    {
        OrdinanceAvailability.Always => MinPopulation > 0 ? $"Available from {MinPopulation:N0} people." : "Always available.",
        OrdinanceAvailability.Unlock => (UnlockText.Length > 0 ? UnlockText : "Unlocked by something in the city.") + (MinPopulation > 0 ? $" Needs {MinPopulation:N0} people." : ""),
        _ => $"Comes up by chance (about {ChancePerYear}% a year from {MinPopulation:N0} people). The offer lasts {OfferMonths} months" + (DurationMonths > 0 ? $"; it runs for {DurationMonths} months." : "."),
    };
}

public enum OrdinanceStatus : byte { Locked = 0, Available = 1, Offered = 2, Enacted = 3 }

/// <summary>Where one ordinance stands in this city. Saved.</summary>
public sealed class OrdinanceState
{
    public bool Unlocked;
    public bool Enacted;
    /// <summary>For random ordinances: the tick the offer ends, or -1 when not offered.</summary>
    public long OfferUntil = -1;
    /// <summary>For time-limited ordinances: the tick it ends, or -1.</summary>
    public long ExpiresAt = -1;
}

/// <summary>An ordinance as the player sees it: status, what it would do now, and the steps behind that.</summary>
public sealed record OrdinanceView(
    OrdinanceDef Def,
    OrdinanceStatus Status,
    int MonthsLeft,
    long MonthlyNet,
    List<string> Effects,
    List<(string Label, string Value)> Trace);

public sealed record SetOrdinanceAction(string Id, bool Enact) : SimAction;

public sealed partial class World
{
    public OrdinanceState[] Ordinances { get; private set; } = Array.Empty<OrdinanceState>();
    /// <summary>Sum of every active ordinance's modifiers. Saved, so a loaded city behaves the same mid-month.</summary>
    public readonly long[] Mods = new long[Enum.GetValues<ModKey>().Length];
    /// <summary>Money the active ordinances moved last time they ran, for the budget. Saved.</summary>
    public readonly List<(string Label, long Amount)> OrdinanceMoney = new();

    public long Mod(ModKey k) => Mods[(int)k];
    /// <summary>Scales a usage by a percent modifier, never below zero.</summary>
    public int Scaled(int value, ModKey k) => Mods[(int)k] == 0 ? value : (int)Math.Max(0, value * (100 + Mods[(int)k]) / 100);

    void InitOrdinances()
    {
        Ordinances = Content.Ordinances.Select(_ => new OrdinanceState()).ToArray();
    }

    OrdinanceStatus StatusOf(int k)
    {
        var s = Ordinances[k];
        if (s.Enacted) return OrdinanceStatus.Enacted;
        var d = Content.Ordinances[k];
        if (d.Kind == OrdinanceAvailability.Random) return s.OfferUntil > Tick ? OrdinanceStatus.Offered : OrdinanceStatus.Locked;
        return s.Unlocked ? OrdinanceStatus.Available : OrdinanceStatus.Locked;
    }

    /// <summary>Monthly: unlocks, random offers, expiry, then the active graphs set this month's modifiers.</summary>
    void UpdateOrdinances()
    {
        for (int k = 0; k < Ordinances.Length; k++)
        {
            var d = Content.Ordinances[k];
            var s = Ordinances[k];
            bool bigEnough = Population >= d.MinPopulation;
            switch (d.Kind)
            {
                case OrdinanceAvailability.Always:
                    if (!s.Unlocked && bigEnough)
                    {
                        s.Unlocked = true;
                        if (Tick > 0) Notices.Add(new SimNotice($"New ordinance available: {d.Name}.", "info"));
                    }
                    break;
                case OrdinanceAvailability.Unlock:
                    if (!s.Unlocked && bigEnough && d.Unlock != null && EvaluateRules(d.Unlock).Last.Number != 0)
                    {
                        s.Unlocked = true;
                        Notices.Add(new SimNotice($"Unlocked a new ordinance: {d.Name}.", "good"));
                    }
                    break;
                case OrdinanceAvailability.Random:
                    if (s.Enacted || s.OfferUntil > Tick || !bigEnough || d.ChancePerYear <= 0) break;
                    if (s.OfferUntil >= 0 && s.OfferUntil <= Tick)
                    {
                        s.OfferUntil = -1;
                        break; // an offer that lapsed can come back, but not the same month
                    }
                    if (Rng.Next(1200) < d.ChancePerYear)
                    {
                        s.OfferUntil = Tick + d.OfferMonths * DaysPerMonth;
                        Notices.Add(new SimNotice($"Offer: {d.Name}. {d.Description} Open Laws to decide.", "info"));
                    }
                    break;
            }
            if (s.Enacted && s.ExpiresAt >= 0 && s.ExpiresAt <= Tick)
            {
                s.Enacted = false;
                s.ExpiresAt = -1;
                s.OfferUntil = -1;
                Notices.Add(new SimNotice($"{d.Name} has ended.", "info"));
            }
        }
        RunOrdinanceGraphs();
    }

    /// <summary>Runs every enacted ordinance's graph and adds up what they do.</summary>
    void RunOrdinanceGraphs()
    {
        Array.Clear(Mods);
        OrdinanceMoney.Clear();
        for (int k = 0; k < Ordinances.Length; k++)
        {
            if (!Ordinances[k].Enacted) continue;
            var o = EvaluateRules(Content.Ordinances[k].Graph);
            for (int m = 0; m < Mods.Length; m++) Mods[m] += o.Mods[m];
            OrdinanceMoney.AddRange(o.Money);
        }
    }

    void ApplySetOrdinance(SetOrdinanceAction a)
    {
        int k = Content.Ordinances.FindIndex(d => d.Id == a.Id);
        if (k < 0) { Notices.Add(new SimNotice($"There is no ordinance called {a.Id}.", "warning")); return; }
        var d = Content.Ordinances[k];
        var s = Ordinances[k];
        var status = StatusOf(k);
        if (a.Enact)
        {
            if (status == OrdinanceStatus.Enacted) return;
            if (status == OrdinanceStatus.Locked)
            {
                Notices.Add(new SimNotice($"{d.Name} isn't available yet. {d.AvailabilityText}", "warning"));
                return;
            }
            s.Enacted = true;
            s.OfferUntil = -1;
            s.ExpiresAt = d.DurationMonths > 0 ? Tick + d.DurationMonths * DaysPerMonth : -1;
            Notices.Add(new SimNotice($"Enacted: {d.Name}.", "good"));
        }
        else
        {
            if (!s.Enacted) return;
            s.Enacted = false;
            s.ExpiresAt = -1;
            Notices.Add(new SimNotice($"Repealed: {d.Name}.", "info"));
        }
        RunOrdinanceGraphs();
    }

    /// <summary>Every ordinance with its live preview: what it would do to this city right now.</summary>
    public List<OrdinanceView> OrdinanceViews()
    {
        var list = new List<OrdinanceView>();
        for (int k = 0; k < Ordinances.Length; k++)
        {
            var d = Content.Ordinances[k];
            var s = Ordinances[k];
            var o = EvaluateRules(d.Graph, trace: true);
            var effects = new List<string>();
            for (int m = 0; m < o.Mods.Length; m++)
                if (o.Mods[m] != 0) effects.Add(RuleGraph.DescribeMod((ModKey)m, o.Mods[m]));
            var status = StatusOf(k);
            long until = status == OrdinanceStatus.Enacted ? s.ExpiresAt : status == OrdinanceStatus.Offered ? s.OfferUntil : -1;
            int months = until < 0 ? 0 : (int)((until - Tick + DaysPerMonth - 1) / DaysPerMonth);
            list.Add(new OrdinanceView(d, status, months, o.Money.Sum(m => m.Amount), effects, o.Trace));
        }
        return list;
    }

    void WriteOrdinances(BinaryWriter w)
    {
        w.Write(Ordinances.Length);
        foreach (var s in Ordinances) { w.Write(s.Unlocked); w.Write(s.Enacted); w.Write(s.OfferUntil); w.Write(s.ExpiresAt); }
        foreach (var m in Mods) w.Write(m);
        w.Write(OrdinanceMoney.Count);
        foreach (var (label, amount) in OrdinanceMoney) { w.Write(label); w.Write(amount); }
    }

    void ReadOrdinances(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n != Ordinances.Length) throw new InvalidDataException("this save was made with different ordinances");
        foreach (var s in Ordinances) { s.Unlocked = r.ReadBoolean(); s.Enacted = r.ReadBoolean(); s.OfferUntil = r.ReadInt64(); s.ExpiresAt = r.ReadInt64(); }
        for (int m = 0; m < Mods.Length; m++) Mods[m] = r.ReadInt64();
        int nm = r.ReadInt32();
        for (int i = 0; i < nm; i++) OrdinanceMoney.Add((r.ReadString(), r.ReadInt64()));
    }
}
