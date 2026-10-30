using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>How the computer plays the city seat on autopilot.</summary>
public enum AutopilotStyle : byte { Growth = 0, Green = 1, Profit = 2, Chaos = 3 }

/// <summary>
/// A controllable thing: the city's mayor seat, or a side game's operator seat. It has an owner (a player's name, or
/// empty for nobody) and is played by its owner (Manual) or by the computer at the level the owner picks.
/// </summary>
public sealed class Seat
{
    public const string Mayor = "mayor";

    public string Id = "";
    public string Owner = "";
    public OperatorLevel Level;
    public AutopilotStyle Style;
    /// <summary>The most the computer may spend in a month on this seat's behalf (autopilot and caretaker).</summary>
    public long SpendCap = DefaultSpendCap;
    /// <summary>Set while the computer plays the seat: what things were like when the owner left.</summary>
    public AwayStart? Away;
    /// <summary>The report from the last time the owner came back, kept until the next hand-off.</summary>
    public AwayReport? Report;

    /// <summary>How the computer plays it while the owner is away from a dedicated server.</summary>
    public OperatorLevel AwayLevel = OperatorLevel.Caretaker;
    public AutopilotStyle AwayStyle;
    /// <summary>The computer took it because the owner left, so it goes back to them when they return.</summary>
    public bool AutoAway;

    public const long DefaultSpendCap = 25_000;
}

/// <summary>Something the computer did while the owner was away, and why.</summary>
public sealed record AwayNote(long Tick, string Text, int X, int Y);

/// <summary>The city when its owner handed it to the computer.</summary>
public sealed class AwayStart
{
    public long Tick;
    public OperatorLevel Level;
    public int Population, Jobs, Approval;
    public long Funds, Debt, HeadlineSeq;
    public int[] TaxTenths = Array.Empty<int>();
    /// <summary>Civic buildings at hand-off, so caretaker can replace like for like: id, type, x, y.</summary>
    public List<(int Id, int Type, int X, int Y)> Civic = new();
    public List<AwayNote> Notes = new();
}

/// <summary>"While you were away": what changed and what the computer did, with its reasons.</summary>
public sealed record AwayReport(long FromTick, long ToTick, OperatorLevel Level, IReadOnlyList<string> Changes, IReadOnlyList<AwayNote> Notes, IReadOnlyList<string> News);

/// <summary>
/// Claims or hands over a seat. Owner empty leaves it to nobody (computer-run). Level is how it is played: manual by
/// its owner, or stasis, caretaker or autopilot by the computer.
/// </summary>
public sealed record SetSeatAction(string SeatId, string Owner, OperatorLevel Level, AutopilotStyle Style = AutopilotStyle.Growth, long SpendCap = Seat.DefaultSpendCap) : SimAction;

public sealed partial class World
{
    public readonly List<Seat> Seats = new();

    public Seat MayorSeat => Seats[0];

    public Seat? SeatOf(string id) => Seats.FirstOrDefault(s => s.Id == id);

    /// <summary>True while the city seat is in stasis: nothing that persists changes, though people keep moving.</summary>
    public bool InStasis => MayorSeat.Level == OperatorLevel.Stasis;

    void InitSeats() => Seats.Add(new Seat { Id = Seat.Mayor, Owner = "player", Level = OperatorLevel.Manual });

    /// <summary>A side game's operator seat, made when it attaches: nobody owns it and the computer keeps it running.</summary>
    void AddSideGameSeat(string gameId)
    {
        if (SeatOf(gameId) == null) Seats.Add(new Seat { Id = gameId, Level = OperatorLevel.Caretaker });
    }

    void FillSeat(HostedGame hg, CityToGame input)
    {
        var seat = SeatOf(hg.Id);
        input.Operator = seat?.Level ?? OperatorLevel.Caretaker;
        input.OwnerName = seat is { Owner.Length: > 0 } ? seat.Owner : null;
    }

    void ApplySetSeat(SetSeatAction a)
    {
        var seat = SeatOf(a.SeatId);
        if (seat == null) { Reject($"There is no seat called '{a.SeatId}'."); return; }
        if (seat.Id == Seat.Mayor && a.Owner.Length == 0) { Reject("The city always has a mayor. Hand it to the computer instead."); return; }
        if (a.Owner.Length == 0 && a.Level == OperatorLevel.Manual) { Reject("Nobody can play a seat by hand if nobody owns it."); return; }
        if (seat.Id != Seat.Mayor && a.Level == OperatorLevel.Stasis) { Reject("Stasis is for the city seat."); return; }
        bool wasAway = seat.Level != OperatorLevel.Manual;
        bool goesAway = a.Level != OperatorLevel.Manual;
        string who = a.Owner.Length > 0 ? a.Owner : "nobody";
        if (seat.Owner != a.Owner && seat.Owner.Length > 0 && a.Owner.Length > 0 && seat.Id != Seat.Mayor)
        {
            Reject($"{seat.Owner} owns that seat. Ask them to hand it over.");
            return;
        }
        seat.Owner = a.Owner;
        seat.AutoAway = false;  // a hand-off by hand (or a new owner) is the owner's own choice, not the computer's
        seat.Style = a.Style;
        seat.SpendCap = Math.Max(0, a.SpendCap);
        if (seat.Id == Seat.Mayor)
        {
            if (!wasAway && goesAway) seat.Away = StartAway(a.Level);
            else if (wasAway && goesAway && seat.Away != null)
                seat.Away.Notes.Add(new AwayNote(Tick, $"Switched from {LevelName(seat.Level)} to {LevelName(a.Level)}.", -1, -1));
            else if (wasAway && !goesAway && seat.Away != null)
            {
                seat.Report = BuildAwayReport(seat.Away);
                seat.Away = null;
            }
        }
        seat.Level = a.Level;
        string seatName = seat.Id == Seat.Mayor ? "The city" : SideGame(seat.Id)?.Game.Manifest.Name ?? seat.Id;
        // Taking the city back is news for its owner alone; everything else is news for everyone.
        if (a.Level == OperatorLevel.Manual && seat.Id == Seat.Mayor)
        {
            Notices.Add(new SimNotice(wasAway ? "You have the city back. Here's what happened while you were away." : "You're running the city.", "info", To: seat.Owner));
            return;
        }
        Notices.Add(new SimNotice(a.Level switch
        {
            OperatorLevel.Manual => $"{who} runs {seatName} now.",
            OperatorLevel.Stasis => $"{seatName} is in stasis: nothing will grow, decay or cost money until {who} takes it back.",
            OperatorLevel.Caretaker => $"{seatName} is on caretaker: the computer keeps things running and changes nothing big.",
            _ => $"{seatName} is on autopilot ({StyleName(a.Style)}): the computer plays it until {who} takes it back.",
        }, "info"));
    }

    public static string LevelName(OperatorLevel l) => l switch
    {
        OperatorLevel.Manual => "manual", OperatorLevel.Stasis => "stasis", OperatorLevel.Caretaker => "caretaker", _ => "autopilot",
    };

    public static string StyleName(AutopilotStyle s) => s switch
    {
        AutopilotStyle.Growth => "growth", AutopilotStyle.Green => "green", AutopilotStyle.Profit => "profit", _ => "chaos",
    };

    /// <summary>Stasis stops the clock for prompts too: each held day pushes their deadlines back a day.</summary>
    void HoldPrompts()
    {
        foreach (var p in Prompts) if (p.Status == PromptStatus.Open) p.Deadline++;
    }

    AwayStart StartAway(OperatorLevel level)
    {
        var a = new AwayStart
        {
            Tick = Tick, Level = level, Population = Population, Jobs = Jobs, Approval = Approval, Funds = Funds, Debt = Debt,
            HeadlineSeq = HeadlineSeq, TaxTenths = (int[])TaxTenths.Clone(),
        };
        foreach (var b in Buildings)
            if (b != null && TypeOf(b).Placeable && TypeOf(b).Category != Category.Landmark && TypeOf(b).Category != Category.Reward)
                a.Civic.Add((b.Id, b.Type, b.X, b.Y));
        return a;
    }

    /// <summary>Notes something the computer did while the city's owner was away.</summary>
    void AwayNoteAdd(string text, int x = -1, int y = -1)
    {
        MayorSeat.Away?.Notes.Add(new AwayNote(Tick, text, x, y));
        Notices.Add(new SimNotice(text, "info", x, y));
    }

    AwayReport BuildAwayReport(AwayStart a)
    {
        string Delta(string label, long from, long to, bool money = false)
        {
            string f(long v) => money ? $"${v:N0}" : v.ToString("N0");
            long d = to - from;
            return $"{label}: {f(from)} → {f(to)} ({(d >= 0 ? "+" : "-")}{f(Math.Abs(d))})";
        }
        int days = (int)(Tick - a.Tick);
        var changes = new List<string>
        {
            $"You were away {days / DaysPerMonth} months and {days % DaysPerMonth} days, with the city on {LevelName(a.Level)}.",
            Delta("Population", a.Population, Population),
            Delta("Jobs", a.Jobs, Jobs),
            Delta("Funds", a.Funds, Funds, money: true),
            Delta("Approval", a.Approval, Approval),
        };
        if (Debt != a.Debt) changes.Add(Delta("Debt", a.Debt, Debt, money: true));
        var taxNames = new[] { "R$", "R$$", "R$$$", "C$", "C$$", "C$$$", "I dirty", "I manufacturing", "I high-tech" };
        var taxChanges = Enumerable.Range(0, 9).Where(k => TaxTenths[k] != a.TaxTenths[k])
            .Select(k => $"{taxNames[k]} {a.TaxTenths[k] / 10.0:0.0}% → {TaxTenths[k] / 10.0:0.0}%").ToList();
        if (taxChanges.Count > 0) changes.Add("Taxes: " + string.Join(", ", taxChanges));
        if (a.Notes.Count == 0) changes.Add("The computer didn't need to do anything.");
        var news = Headlines.Where(h => h.Seq > a.HeadlineSeq).Select(h => h.Text).ToList();
        return new AwayReport(a.Tick, Tick, a.Level, changes, a.Notes.ToList(), news);
    }

    void WriteSeats(BinaryWriter w)
    {
        w.Write(Seats.Count);
        foreach (var s in Seats)
        {
            w.Write(s.Id); w.Write(s.Owner); w.Write((byte)s.Level); w.Write((byte)s.Style); w.Write(s.SpendCap);
            w.Write((byte)s.AwayLevel); w.Write((byte)s.AwayStyle); w.Write(s.AutoAway);
            w.Write(s.Away != null);
            if (s.Away is { } a)
            {
                w.Write(a.Tick); w.Write((byte)a.Level); w.Write(a.Population); w.Write(a.Jobs); w.Write(a.Approval);
                w.Write(a.Funds); w.Write(a.Debt); w.Write(a.HeadlineSeq);
                foreach (var t in a.TaxTenths) w.Write(t);
                w.Write(a.Civic.Count);
                foreach (var c in a.Civic) { w.Write(c.Id); w.Write(c.Type); w.Write(c.X); w.Write(c.Y); }
                WriteNotes(w, a.Notes);
            }
            w.Write(s.Report != null);
            if (s.Report is { } r)
            {
                w.Write(r.FromTick); w.Write(r.ToTick); w.Write((byte)r.Level);
                w.Write(r.Changes.Count); foreach (var c in r.Changes) w.Write(c);
                WriteNotes(w, r.Notes);
                w.Write(r.News.Count); foreach (var n in r.News) w.Write(n);
            }
        }
    }

    static void WriteNotes(BinaryWriter w, IReadOnlyList<AwayNote> notes)
    {
        w.Write(notes.Count);
        foreach (var n in notes) { w.Write(n.Tick); w.Write(n.Text); w.Write(n.X); w.Write(n.Y); }
    }

    static List<AwayNote> ReadNotes(BinaryReader r)
    {
        int n = r.ReadInt32();
        var list = new List<AwayNote>(n);
        for (int i = 0; i < n; i++) list.Add(new AwayNote(r.ReadInt64(), r.ReadString(), r.ReadInt32(), r.ReadInt32()));
        return list;
    }

    void ReadSeats(BinaryReader r)
    {
        Seats.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var s = new Seat { Id = r.ReadString(), Owner = r.ReadString(), Level = (OperatorLevel)r.ReadByte(), Style = (AutopilotStyle)r.ReadByte(), SpendCap = r.ReadInt64() };
            (s.AwayLevel, s.AwayStyle, s.AutoAway) = ((OperatorLevel)r.ReadByte(), (AutopilotStyle)r.ReadByte(), r.ReadBoolean());
            if (r.ReadBoolean())
            {
                var a = new AwayStart
                {
                    Tick = r.ReadInt64(), Level = (OperatorLevel)r.ReadByte(), Population = r.ReadInt32(), Jobs = r.ReadInt32(), Approval = r.ReadInt32(),
                    Funds = r.ReadInt64(), Debt = r.ReadInt64(), HeadlineSeq = r.ReadInt64(), TaxTenths = new int[9],
                };
                for (int k = 0; k < 9; k++) a.TaxTenths[k] = r.ReadInt32();
                int nc = r.ReadInt32();
                for (int k = 0; k < nc; k++) a.Civic.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
                a.Notes = ReadNotes(r);
                s.Away = a;
            }
            if (r.ReadBoolean())
            {
                long from = r.ReadInt64(), to = r.ReadInt64();
                var level = (OperatorLevel)r.ReadByte();
                var changes = new List<string>();
                int nc = r.ReadInt32();
                for (int k = 0; k < nc; k++) changes.Add(r.ReadString());
                var notes = ReadNotes(r);
                var news = new List<string>();
                int nn = r.ReadInt32();
                for (int k = 0; k < nn; k++) news.Add(r.ReadString());
                s.Report = new AwayReport(from, to, level, changes, notes, news);
            }
            Seats.Add(s);
        }
    }
}
