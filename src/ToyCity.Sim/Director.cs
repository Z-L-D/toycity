using System.Text.Json;

namespace ToyCity.Sim;

public enum EventKind : byte { Petition = 0, Deal = 1, News = 2 }

/// <summary>What happens when an event resolves: a graph run once, whose modifiers may keep running for a while.</summary>
public sealed class OutcomeDef
{
    public RuleGraph Graph = new();
    public int DurationMonths;
}

/// <summary>An event template from content/events.json. The director decides when; the voice decides the words.</summary>
public sealed class EventDef
{
    public string Id = "";
    public EventKind Kind;
    public int Weight = 1;
    public int CooldownMonths;
    public int MinPopulation;
    public int DeadlineMonths = 6;
    /// <summary>Where the event happens: "home", "shop", "factory" or "road", the lowest or highest of a layer. Empty for none.</summary>
    public string LocateSet = "", LocateLayer = "";
    public bool LocateHighest;
    public RuleGraph Trigger = new();
    public RuleGraph? Goal;
    public string GoalText = "";
    /// <summary>Fulfilled (petition) or accepted (deal).</summary>
    public OutcomeDef? Yes;
    /// <summary>Ignored (petition) or declined (deal).</summary>
    public OutcomeDef? No;
    public int Index;

    public IEnumerable<RuleGraph> Graphs
    {
        get
        {
            yield return Trigger;
            if (Goal != null) yield return Goal;
            if (Yes != null) yield return Yes.Graph;
            if (No != null) yield return No.Graph;
        }
    }

    public static (List<EventDef>, DirectorSettings) Parse(string json)
    {
        var list = new List<EventDef>();
        var settings = new DirectorSettings();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.TryGetProperty("director", out var d))
        {
            int Get(string n, int def) => d.TryGetProperty(n, out var v) ? v.GetInt32() : def;
            settings = new DirectorSettings(Get("chancePercent", 40), Get("minMonthsBetween", 2), Get("maxOpen", 3), Get("newsChancePercent", 30));
        }
        var ids = new HashSet<string>();
        foreach (var e in root.GetProperty("events").EnumerateArray())
        {
            var ev = new EventDef { Id = e.GetProperty("id").GetString()!, Index = list.Count };
            if (!ids.Add(ev.Id)) throw new InvalidDataException($"duplicate event {ev.Id}");
            ev.Kind = e.GetProperty("kind").GetString() switch
            {
                "petition" => EventKind.Petition,
                "deal" => EventKind.Deal,
                "news" => EventKind.News,
                var k => throw new InvalidDataException($"{ev.Id}: unknown kind '{k}'"),
            };
            int Int(string n, int def) => e.TryGetProperty(n, out var v) ? v.GetInt32() : def;
            ev.Weight = Math.Max(1, Int("weight", 1));
            ev.CooldownMonths = Int("cooldownMonths", 12);
            ev.MinPopulation = Int("minPopulation", 0);
            ev.DeadlineMonths = Math.Max(1, Int("deadlineMonths", 6));
            if (e.TryGetProperty("locate", out var loc))
            {
                var parts = loc.GetString()!.Split(':');
                var head = parts[0].Split('.');
                if (parts.Length != 2 || head.Length != 2 || head[0] is not ("home" or "shop" or "factory" or "road")
                    || head[1] is not ("lowest" or "highest") || !RuleGraph.Layers.Contains(parts[1]))
                    throw new InvalidDataException($"{ev.Id}: locate must look like home.lowest:parks");
                ev.LocateSet = head[0];
                ev.LocateHighest = head[1] == "highest";
                ev.LocateLayer = parts[1];
            }
            ev.Trigger = e.TryGetProperty("trigger", out var tr) ? RuleGraph.Compile(tr, ev.Id + " trigger") : new RuleGraph();
            if (e.TryGetProperty("goal", out var g)) ev.Goal = RuleGraph.Compile(g, ev.Id + " goal");
            if (e.TryGetProperty("goalText", out var gt)) ev.GoalText = gt.GetString()!;
            OutcomeDef? Outcome(string name)
            {
                if (!e.TryGetProperty(name, out var o)) return null;
                return new OutcomeDef
                {
                    Graph = RuleGraph.Compile(o.GetProperty("nodes"), $"{ev.Id} {name}"),
                    DurationMonths = o.TryGetProperty("durationMonths", out var dm) ? dm.GetInt32() : 0,
                };
            }
            ev.Yes = Outcome(ev.Kind == EventKind.Petition ? "onFulfilled" : "onAccept");
            ev.No = Outcome(ev.Kind == EventKind.Petition ? "onIgnored" : "onDecline");
            if (ev.Kind == EventKind.Petition && ev.Goal == null) throw new InvalidDataException($"{ev.Id}: a petition needs a goal");
            list.Add(ev);
        }
        return (list, settings);
    }
}

public sealed record DirectorSettings(int ChancePercent = 40, int MinMonthsBetween = 2, int MaxOpen = 3, int NewsChancePercent = 30);

public enum PromptStatus : byte { Open = 0, Fulfilled = 1, Ignored = 2, Accepted = 3, Declined = 4 }

/// <summary>A petition or deal put to the mayor. Saved, including the words, so history reads the same after loading.</summary>
public sealed class Prompt
{
    public int Id;
    public int Event;
    public EventKind Kind;
    public int X = -1, Y = -1;
    public long Created, Deadline, Resolved = -1;
    public string Petitioner = "";
    public string Text = "";
    public string ResultText = "";
    public PromptStatus Status;
}

/// <summary>An event outcome whose modifiers keep running for a while, like a temporary ordinance.</summary>
public sealed record ActiveEffect(int Event, bool Yes, long Until);

/// <summary>A line for the news ticker.</summary>
public sealed record Headline(long Seq, long Tick, string Text, string Severity, int X, int Y);

public sealed record RespondPromptAction(int PromptId, bool Accept) : SimAction;

public sealed partial class World
{
    public const int MaxHeadlines = 40;
    public const int MaxPromptHistory = 20;

    public readonly List<Prompt> Prompts = new();
    public readonly List<ActiveEffect> ActiveEffects = new();
    public readonly List<Headline> Headlines = new();
    public long HeadlineSeq;
    int _nextPromptId = 1;
    long _lastPromptTick = long.MinValue / 2;
    long[] _eventLastFired = Array.Empty<long>();
    /// <summary>Which placeable building types the mayor may build. Saved.</summary>
    public bool[] TypeUnlocked { get; private set; } = Array.Empty<bool>();
    /// <summary>Mayor approval, 0..100, worked out monthly. Saved.</summary>
    public int Approval = 50;
    /// <summary>Feelings from petitions and deals, added to approval and fading by a sixth each month. Saved.</summary>
    public int Mood;

    void InitDirector()
    {
        _eventLastFired = Content.Events.Select(_ => long.MinValue / 2).ToArray();
        TypeUnlocked = Content.Types.Select(t => t.Unlock == null).ToArray();
    }

    ulong VoiceSeed(long a, long b) => Seed * 0x9E3779B97F4A7C15UL ^ (ulong)a * 0xBF58476D1CE4E5B9UL ^ (ulong)b;

    /// <summary>Adds a ticker headline. Also used by other systems for milestones, laws and unlocks.</summary>
    public void AddHeadline(string text, string severity = "info", int x = -1, int y = -1)
    {
        Headlines.Add(new Headline(++HeadlineSeq, Tick, text, severity, x, y));
        if (Headlines.Count > MaxHeadlines) Headlines.RemoveAt(0);
    }

    internal string Say(string key, long a, long b, Dictionary<string, string>? facts = null)
    {
        facts ??= new();
        facts.TryAdd("city", Name);
        facts.TryAdd("population", Population.ToString("N0"));
        return Content.Voice.Say(key, VoiceSeed(a, b), facts);
    }

    /// <summary>Monthly: unlock buildings whose conditions are met.</summary>
    void CheckUnlocks()
    {
        for (int t = 0; t < TypeUnlocked.Length; t++)
        {
            if (TypeUnlocked[t] || Content.Types[t].Unlock is not { } g) continue;
            if (EvaluateRules(g).Last.Number == 0) continue;
            Unlock(t);
        }
    }

    void Unlock(int t)
    {
        if (TypeUnlocked[t]) return;
        TypeUnlocked[t] = true;
        var type = Content.Types[t];
        string text = Say("news.unlock", Tick, t, new() { ["building"] = type.Name });
        Notices.Add(new SimNotice(text, "good"));
        AddHeadline(text, "good");
    }

    /// <summary>
    /// Monthly: resolves open petitions and deals, then perhaps raises one new prompt and one news item. Rate-limited:
    /// a few months between prompts, a cap on how many are open, and a cooldown per template.
    /// </summary>
    void RunDirector()
    {
        foreach (var p in Prompts)
        {
            if (p.Status != PromptStatus.Open) continue;
            var ev = Content.Events[p.Event];
            if (ev.Kind == EventKind.Petition && EvaluateRules(ev.Goal!, new RuleContext(p.X, p.Y)).Last.Number != 0)
                Resolve(p, PromptStatus.Fulfilled);
            else if (Tick >= p.Deadline)
                Resolve(p, ev.Kind == EventKind.Petition ? PromptStatus.Ignored : PromptStatus.Declined);
        }
        while (Prompts.Count(p => p.Status != PromptStatus.Open) > MaxPromptHistory)
            Prompts.RemoveAt(Prompts.FindIndex(p => p.Status != PromptStatus.Open));

        var set = Content.Director;
        int open = Prompts.Count(p => p.Status == PromptStatus.Open);
        if (open < set.MaxOpen && Tick - _lastPromptTick >= set.MinMonthsBetween * DaysPerMonth && Rng.Next(100) < set.ChancePercent)
            Raise(e => e.Kind != EventKind.News);
        if (Rng.Next(100) < set.NewsChancePercent)
            Raise(e => e.Kind == EventKind.News);
    }

    /// <summary>Picks an eligible template by weight and raises it. Eligibility: population, cooldown, location, trigger.</summary>
    void Raise(Func<EventDef, bool> filter)
    {
        var eligible = new List<(EventDef Ev, int X, int Y, Building? At)>();
        foreach (var ev in Content.Events)
        {
            if (!filter(ev) || Population < ev.MinPopulation) continue;
            if (Tick - _eventLastFired[ev.Index] < ev.CooldownMonths * DaysPerMonth) continue;
            if (Prompts.Any(p => p.Status == PromptStatus.Open && p.Event == ev.Index)) continue;
            int x = -1, y = -1;
            Building? at = null;
            if (ev.LocateSet.Length > 0)
            {
                (x, y, at) = Locate(ev);
                if (x < 0) continue;
            }
            if (EvaluateRules(ev.Trigger, new RuleContext(x, y)).Last.Number == 0) continue;
            eligible.Add((ev, x, y, at));
        }
        if (eligible.Count == 0) return;
        int total = eligible.Sum(e => e.Ev.Weight);
        int roll = Rng.Next(total);
        var pick = eligible[0];
        foreach (var e in eligible)
        {
            if (roll < e.Ev.Weight) { pick = e; break; }
            roll -= e.Ev.Weight;
        }
        _eventLastFired[pick.Ev.Index] = Tick;

        if (pick.Ev.Kind == EventKind.News)
        {
            AddHeadline(Say(pick.Ev.Id, Tick, pick.Ev.Index, Facts(pick.Ev, pick.X, pick.Y, pick.At, VoiceSeed(Tick, pick.Ev.Index))), "info", pick.X, pick.Y);
            return;
        }
        var prompt = new Prompt
        {
            Id = _nextPromptId++, Event = pick.Ev.Index, Kind = pick.Ev.Kind, X = pick.X, Y = pick.Y,
            Created = Tick, Deadline = Tick + pick.Ev.DeadlineMonths * DaysPerMonth,
        };
        var facts = Facts(pick.Ev, pick.X, pick.Y, pick.At, VoiceSeed(prompt.Id, 0));
        prompt.Petitioner = facts["name"];
        prompt.Text = Say(pick.Ev.Id, prompt.Id, 0, facts);
        Prompts.Add(prompt);
        _lastPromptTick = Tick;
        Notices.Add(new SimNotice(prompt.Text, "info", prompt.X, prompt.Y));
        AddHeadline(prompt.Text, "info", prompt.X, prompt.Y);
    }

    Dictionary<string, string> Facts(EventDef ev, int x, int y, Building? at, ulong seed)
    {
        var facts = new Dictionary<string, string> { ["name"] = Content.Voice.Name(seed) };
        if (x >= 0) facts["street"] = Content.Voice.Street(x, y);
        int count = at != null ? Math.Max(at.Residents, at.Jobs) : x >= 0 ? Trips[Idx(x, y)] : 0;
        facts["count"] = Math.Max(3, count).ToString("N0");
        var yes = ev.Yes != null ? EvaluateRules(ev.Yes.Graph, new RuleContext(x, y)) : null;
        facts["amount"] = (yes?.Grants.Sum(g => g.Amount) ?? 0).ToString("N0");
        return facts;
    }

    /// <summary>
    /// Finds the event's spot: the home, shop, factory or road with the lowest or highest value of a layer. Ties
    /// (common when a whole town lacks something) are broken with the city's random numbers, so petitions come from
    /// different streets.
    /// </summary>
    (int X, int Y, Building? At) Locate(EventDef ev)
    {
        long best = 0;
        var ties = new List<(int X, int Y, Building? At)>();
        void Consider(long v, int x, int y, Building? b)
        {
            if (ties.Count > 0 && (ev.LocateHighest ? v < best : v > best)) return;
            if (ties.Count == 0 || v != best) { ties.Clear(); best = v; }
            ties.Add((x, y, b));
        }
        if (ev.LocateSet == "road")
        {
            for (int i = 0; i < N; i++)
                if (ProfileAt(i) is { Cars: true }) Consider(LayerAt(ev.LocateLayer, i), i % Width, i / Width, null);
        }
        else
        {
            var zone = ev.LocateSet switch { "home" => Zone.Residential, "shop" => Zone.Commercial, _ => Zone.Industrial };
            foreach (var b in Buildings)
            {
                if (b == null || !b.Operating) continue;
                var t = TypeOf(b);
                if (t.Placeable || t.Zone != zone) continue;
                Consider(LayerAt(ev.LocateLayer, Idx(b.X, b.Y)), b.X, b.Y, b);
            }
        }
        return ties.Count == 0 ? (-1, -1, null) : ties[Rng.Next(ties.Count)];
    }

    void Resolve(Prompt p, PromptStatus status)
    {
        var ev = Content.Events[p.Event];
        bool yes = status is PromptStatus.Fulfilled or PromptStatus.Accepted;
        var outcome = yes ? ev.Yes : ev.No;
        p.Status = status;
        p.Resolved = Tick;
        var facts = Facts(ev, p.X, p.Y, null, VoiceSeed(p.Id, 0));
        facts["name"] = p.Petitioner;
        if (outcome != null)
        {
            var o = EvaluateRules(outcome.Graph, new RuleContext(p.X, p.Y));
            foreach (var (label, amount) in o.Grants)
            {
                Funds += amount;
                OneOffs += amount;
                facts["amount"] = amount.ToString("N0");
            }
            Mood = Math.Clamp(Mood + (int)o.Mood, -50, 50);
            foreach (var id in o.Unlocks) Unlock(Content.TypeIndex(id));
            if (outcome.DurationMonths > 0)
            {
                ActiveEffects.Add(new ActiveEffect(ev.Index, yes, Tick + outcome.DurationMonths * DaysPerMonth));
                RunOrdinanceGraphs();
            }
        }
        string suffix = status switch
        {
            PromptStatus.Fulfilled => "fulfilled", PromptStatus.Ignored => "ignored", PromptStatus.Accepted => "accepted", _ => "declined",
        };
        p.ResultText = Say($"{ev.Id}.{suffix}", p.Id, 1, facts);
        string sev = yes ? "good" : ev.Kind == EventKind.Petition ? "warning" : "info";
        Notices.Add(new SimNotice(p.ResultText, sev, p.X, p.Y));
        AddHeadline(p.ResultText, sev, p.X, p.Y);
    }

    void ApplyRespondPrompt(RespondPromptAction a)
    {
        var p = Prompts.FirstOrDefault(q => q.Id == a.PromptId && q.Status == PromptStatus.Open);
        if (p == null) { Reject("That request has already been settled."); return; }
        if (p.Kind == EventKind.Deal) Resolve(p, a.Accept ? PromptStatus.Accepted : PromptStatus.Declined);
        else if (!a.Accept) Resolve(p, PromptStatus.Ignored);
    }

    /// <summary>
    /// Mayor approval with its reasons. Homes' desirability, taxes, services, jobs, landmarks and the mood left by
    /// petitions and deals. The monthly update and the advisors' panel both call this.
    /// </summary>
    public int ApprovalOf(List<Factor>? f)
    {
        int total = 0;
        void Add(string label, int v) { if (v == 0) return; total += v; f?.Add(new Factor(label, v)); }
        long des = 0, police = 0, health = 0, edu = 0, people = 0;
        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating || b.Residents == 0) continue;
            int i = Idx(b.X, b.Y);
            des += (long)DesR[i] * b.Residents;
            police += Coverage[(int)Service.Police][i] * (long)b.Residents;
            health += Coverage[(int)Service.Health][i] * (long)b.Residents;
            edu += Coverage[(int)Service.Education][i] * (long)b.Residents;
            people += b.Residents;
        }
        Add("Base", 45);
        if (people > 0)
        {
            Add("How nice homes are", (int)Math.Clamp(des / people / 3, -25, 25));
            Add("Services near homes", (int)((police + health + edu) * 100 / 255 / people / 20));
        }
        Add("Taxes", -(int)((StatOf("taxR") - 90) / 5));
        int jobless = LaborForce > 0 ? (LaborForce - Workers) * 100 / LaborForce : 0;
        if (jobless > 5) Add("Unemployment", -Math.Min(15, jobless - 5));
        int landmarks = Buildings.Count(b => b != null && b.Operating && TypeOf(b).Category is Category.Landmark or Category.Reward && TypeOf(b).Attraction > 0);
        Add("Landmarks", Math.Min(10, landmarks * 2));
        Add("Petitions and deals", Mood);
        if (Funds < 0) Add("City in the red", -10);
        return Math.Clamp(total, 0, 100);
    }

    /// <summary>Monthly, after fields: approval, and the mood fading back toward zero.</summary>
    void UpdateApproval()
    {
        Approval = ApprovalOf(null);
        Mood = Mood * 5 / 6;
    }

    void WriteDirector(BinaryWriter w)
    {
        w.Write(Approval); w.Write(Mood); w.Write(HeadlineSeq); w.Write(_nextPromptId); w.Write(_lastPromptTick);
        w.Write(_eventLastFired.Length);
        foreach (var t in _eventLastFired) w.Write(t);
        w.Write(TypeUnlocked.Length);
        foreach (var u in TypeUnlocked) w.Write(u);
        w.Write(Prompts.Count);
        foreach (var p in Prompts)
        {
            w.Write(p.Id); w.Write(p.Event); w.Write((byte)p.Kind); w.Write(p.X); w.Write(p.Y);
            w.Write(p.Created); w.Write(p.Deadline); w.Write(p.Resolved);
            w.Write(p.Petitioner); w.Write(p.Text); w.Write(p.ResultText); w.Write((byte)p.Status);
        }
        w.Write(ActiveEffects.Count);
        foreach (var e in ActiveEffects) { w.Write(e.Event); w.Write(e.Yes); w.Write(e.Until); }
        w.Write(Headlines.Count);
        foreach (var h in Headlines) { w.Write(h.Seq); w.Write(h.Tick); w.Write(h.Text); w.Write(h.Severity); w.Write(h.X); w.Write(h.Y); }
    }

    void ReadDirector(BinaryReader r)
    {
        Approval = r.ReadInt32(); Mood = r.ReadInt32(); HeadlineSeq = r.ReadInt64(); _nextPromptId = r.ReadInt32(); _lastPromptTick = r.ReadInt64();
        int ne = r.ReadInt32();
        if (ne != _eventLastFired.Length) throw new InvalidDataException("this save was made with different events");
        for (int i = 0; i < ne; i++) _eventLastFired[i] = r.ReadInt64();
        int nt = r.ReadInt32();
        if (nt != TypeUnlocked.Length) throw new InvalidDataException("this save was made with different buildings");
        for (int i = 0; i < nt; i++) TypeUnlocked[i] = r.ReadBoolean();
        int np = r.ReadInt32();
        for (int i = 0; i < np; i++)
        {
            Prompts.Add(new Prompt
            {
                Id = r.ReadInt32(), Event = r.ReadInt32(), Kind = (EventKind)r.ReadByte(), X = r.ReadInt32(), Y = r.ReadInt32(),
                Created = r.ReadInt64(), Deadline = r.ReadInt64(), Resolved = r.ReadInt64(),
                Petitioner = r.ReadString(), Text = r.ReadString(), ResultText = r.ReadString(), Status = (PromptStatus)r.ReadByte(),
            });
        }
        int na = r.ReadInt32();
        for (int i = 0; i < na; i++) ActiveEffects.Add(new ActiveEffect(r.ReadInt32(), r.ReadBoolean(), r.ReadInt64()));
        int nh = r.ReadInt32();
        for (int i = 0; i < nh; i++) Headlines.Add(new Headline(r.ReadInt64(), r.ReadInt64(), r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32()));
    }
}
