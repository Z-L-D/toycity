using ToyCity.Protocol.V0;
using PAction = ToyCity.Protocol.V0.Action;

namespace ToyCity.ClientModel.Panels;

/// <summary>
/// Everything the side panel and its dialogs know, with no engine types so it can be tested: the last reply of each
/// kind from the server, what the player has open, and what a click on each link does. The panel is text with
/// links (Godot's BBCode, see <see cref="Bb"/>); <see cref="Render"/> writes it and <see cref="Click"/> runs a link.
/// It covers what the web client's panels do: Why and Inspect with upgrades, gauges, funding, leases and residents;
/// Demand; Alerts; the Forecast and what-if planning; People, with petitions, deals, advisors and the news; Laws;
/// the Budget; map Layers; the Guide; who runs the city, house rules and the "while you were away" report; and
/// new, save and load.
/// </summary>
public sealed partial class PanelModel
{
    public static readonly (string Id, string Name)[] Tabs =
    {
        ("why", "Why"), ("inspect", "Inspect"), ("demand", "Demand"), ("alerts", "Alerts"), ("forecast", "Forecast"),
        ("people", "People"), ("laws", "Laws"), ("budget", "Budget"), ("layers", "Layers"), ("guide", "Guide"), ("buses", "Buses"),
    };

    readonly CityMirror _city;
    readonly Func<ClientMessage, uint> _send;

    /// <summary>Who this client plays as: "player" in solo play, the account name on a dedicated server.</summary>
    public Func<string> Me { get; set; } = () => "player";
    /// <summary>True on a dedicated server, where seats go to the computer while their owner is away.</summary>
    public Func<bool> OnServer { get; set; } = () => false;

    public PanelModel(CityMirror city, Func<ClientMessage, uint> send)
    {
        _city = city;
        _send = send;
        Company = new CompanyModel(city, send, Changed) { Me = () => Me() };
        Company.Toast += (t, sev) => Toast?.Invoke(t, sev);
        Company.JumpTo += (x, y) => JumpTo?.Invoke(x, y);
        Emergency = new EmergencyModel(send, Changed) { Me = () => Me() };
        Region = new RegionModel(send, Changed) { Me = () => Me() };
        Emergency.Toast += (t, sev) => Toast?.Invoke(t, sev);
        Emergency.JumpTo += (x, y) => JumpTo?.Invoke(x, y);
        Emergency.Flash += (x0, y0, x1, y1) => Flash?.Invoke(new Rect { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 });
        // A scene of the player's brings up the service's screen, unless they're busy in a dialog or the company's.
        Emergency.SceneOpened += () => { if (Dialog == null && !Company.Active) OpenDialog("dispatch"); };
    }

    /// <summary>The bus company's own view, which can take the panel's place.</summary>
    public readonly CompanyModel Company;
    /// <summary>The region map, which takes the whole screen while it's open.</summary>
    public readonly RegionModel Region;
    /// <summary>The emergency services' dispatch board, shown in the "dispatch" dialog.</summary>
    public readonly EmergencyModel Emergency;

    /// <summary>Goes up whenever the panel would read differently, so the view knows to redraw it.</summary>
    public int Version { get; private set; }
    void Changed() => Version++;

    public string Tab { get; private set; } = "why";
    /// <summary>The dialog over the map: "seat", "consent", "council", "dispatch", "report", "terms", "offer", "confirm", "new", "save" or "load"; null when none.</summary>
    public string? Dialog { get; private set; }
    /// <summary>Everyone the server knows of in this city, from its presence list, for picking deputies.</summary>
    public List<string> Players { get; set; } = new();

    // What the panel asks of the view around it.
    public event Action<int, int>? JumpTo;
    public event Action<Rect>? Flash;
    /// <summary>Choose a building for the place tool; centre on x, y unless x is -1.</summary>
    public event Action<string, int, int>? PickBuilding;
    public event Action<string, string>? Toast;
    /// <summary>The copy of the city this player took when they de-linked: its save name and the save itself.</summary>
    public event Action<string, byte[]>? CopyArrived;
    /// <summary>The unlocked buildings changed, so the place tool's list should be rebuilt.</summary>
    public event System.Action? UnlocksChanged;

    // ---------------------------------------------------------------------------------------------------------
    // The server's replies

    public WhyResult? Why { get; private set; }
    public InspectResult? Inspect { get; private set; }
    public DemandReport? Demand { get; private set; }
    public IReadOnlyList<Alert> Alerts { get; private set; } = Array.Empty<Alert>();
    public ForecastResult? Forecast { get; private set; }
    public IReadOnlyList<Article> Articles { get; private set; } = Array.Empty<Article>();
    public IReadOnlyList<Ordinance> Ordinances { get; private set; } = Array.Empty<Ordinance>();
    public CivicState? Civic { get; private set; }
    public AdvisorList? Advisors { get; private set; }
    public AgentRoster? Roster { get; private set; }
    public OverlayData? Overlay { get; private set; }
    public IReadOnlyList<string> Saves { get; private set; } = Array.Empty<string>();
    /// <summary>The news, newest first.</summary>
    public IReadOnlyList<Headline> Headlines { get; private set; } = Array.Empty<Headline>();

    /// <summary>The cell the query tool picked, whose Why and Inspect the panel shows.</summary>
    public (int X, int Y)? Selected { get; private set; }
    public int ActiveOverlay { get; private set; }
    public int OverlayVersion { get; private set; }
    bool _showAgents = true, _showUnderground, _showRoutes;
    public bool ShowRoutes { get => _showRoutes; set { _showRoutes = value; Changed(); } }
    public bool ShowAgents { get => _showAgents; set { _showAgents = value; Changed(); } }
    public bool ShowUnderground { get => _showUnderground; set { _showUnderground = value; Changed(); } }
    public long? Followed { get; private set; }
    public string FollowedName { get; private set; } = "";
    public string FollowedJob { get; private set; } = "";
    /// <summary>The last "while you were away" report shown, so it shows once; the view keeps it between runs.</summary>
    public long ShownReport { get; set; } = -1;

    int _lastOverlayMonth = -1;

    /// <summary>Takes one message from the server. Call for every message, after the city mirror has applied it.</summary>
    public void Handle(ServerMessage m)
    {
        Region.Handle(m);
        switch (m.BodyCase)
        {
            case ServerMessage.BodyOneofCase.Full:
                if (ActiveOverlay != 0) RequestOverlay();
                break;
            case ServerMessage.BodyOneofCase.Delta:
                if (_city.Stats.Day == 1)
                {
                    if (Selected is { } s) Query(s.X, s.Y, false);
                    if (Tab is "demand" or "guide" or "laws" or "people") Refresh(Tab);
                }
                if (ActiveOverlay != 0 && _city.Stats.Month != _lastOverlayMonth) RequestOverlay();
                if (Tab == "budget") Changed();
                break;
            case ServerMessage.BodyOneofCase.Why: Why = m.Why; break;
            case ServerMessage.BodyOneofCase.Inspect: Inspect = m.Inspect; break;
            case ServerMessage.BodyOneofCase.Demand: Demand = m.Demand; break;
            case ServerMessage.BodyOneofCase.Alerts: Alerts = m.Alerts.Alerts; break;
            case ServerMessage.BodyOneofCase.Forecast:
                if (_compare is { } c && (m.ReplyTo == c.BaseSeq || m.ReplyTo == c.PlanSeq))
                {
                    if (m.ReplyTo == c.BaseSeq) c.Base = m.Forecast; else c.Plan = m.Forecast;
                }
                else { Forecast = m.Forecast; _forecastBusy = false; }
                break;
            case ServerMessage.BodyOneofCase.Encyclopedia: Articles = m.Encyclopedia.Articles; break;
            case ServerMessage.BodyOneofCase.Ordinances: Ordinances = m.Ordinances.Ordinances; break;
            case ServerMessage.BodyOneofCase.Advisors: Advisors = m.Advisors; break;
            case ServerMessage.BodyOneofCase.Civic: OnCivic(m.Civic); break;
            case ServerMessage.BodyOneofCase.DelinkCopy:
                CopyArrived?.Invoke(m.DelinkCopy.Name, m.DelinkCopy.Save.ToByteArray());
                Toast?.Invoke($"Your copy of the city is saved as \"{m.DelinkCopy.Name}\". Load it on your own server to play it.", "good");
                break;
            case ServerMessage.BodyOneofCase.Agents:
                Roster = m.Agents;
                if (Followed is { } f && Roster.Trips.All(t => t.Id != f)) Followed = null;
                break;
            case ServerMessage.BodyOneofCase.Overlay:
                if (m.Overlay.Kind == (OverlayKind)ActiveOverlay) { Overlay = m.Overlay; OverlayVersion++; }
                break;
            case ServerMessage.BodyOneofCase.Saves: Saves = m.Saves.Names; break;
            case ServerMessage.BodyOneofCase.SideGame: Company.Handle(m.SideGame); Emergency.Handle(m.SideGame); return;
            // A refusal ends whatever the board was waiting on; the tools show why.
            case ServerMessage.BodyOneofCase.Error: Emergency.Refused(); return;
            default: return;
        }
        Changed();
    }

    void OnCivic(CivicState c)
    {
        Civic = c;
        var before = string.Join(",", _city.Unlocked.Order());
        _city.Unlocked = new HashSet<string>(c.UnlockedTypes);
        if (string.Join(",", _city.Unlocked.Order()) != before) UnlocksChanged?.Invoke();
        Headlines = c.Headlines.Reverse().ToList();
        // The report shows once, when the owner takes the city back.
        if (Mayor is { Level: SeatLevel.SeatManual, Report: { } r } && r.ToTick > 0 && r.ToTick != ShownReport)
        {
            ShownReport = r.ToTick;
            Dialog = "report";
        }
    }

    public SeatState? Mayor => Civic?.Seats.FirstOrDefault(s => s.Id == "mayor");
    public int OpenPrompts => Civic?.Prompts.Count(p => p.Status == PromptStatus.PromptOpen) ?? 0;

    // ---------------------------------------------------------------------------------------------------------
    // Asking the server

    /// <summary>Asks why the cell is as it is, and what's on it, and shows the answer.</summary>
    public void Query(int x, int y, bool switchTab = true)
    {
        Selected = (x, y);
        _send(new ClientMessage { Why = new WhyRequest { X = x, Y = y } });
        _send(new ClientMessage { Inspect = new InspectRequest { X = x, Y = y } });
        if (switchTab && Tab != "inspect") Tab = "why";
        Dialog = null;
        Changed();
    }

    /// <summary>Opens a tab, asking the server for fresh data when the tab needs it.</summary>
    public void Open(string tab)
    {
        Tab = tab;
        Refresh(tab);
        Changed();
    }

    void Refresh(string tab)
    {
        switch (tab)
        {
            case "demand": _send(new ClientMessage { Demand = new DemandRequest() }); break;
            case "laws": _send(new ClientMessage { Ordinances = new OrdinancesRequest() }); break;
            case "people": _send(new ClientMessage { Advisors = new AdvisorsRequest() }); break;
            case "guide": _send(new ClientMessage { Encyclopedia = new EncyclopediaRequest() }); break;
        }
    }

    void RequestOverlay()
    {
        _lastOverlayMonth = _city.Stats.Month;
        _send(new ClientMessage { Overlay = new OverlayRequest { Kind = (OverlayKind)ActiveOverlay } });
    }

    void Send(PAction a) => _send(new ClientMessage { Action = a });

    // ---------------------------------------------------------------------------------------------------------
    // What-if planning: while it's on, the tools' actions go into the plan instead of to the city

    public bool Planning { get; private set; }
    readonly List<(PAction Action, string Label)> _plan = new();
    public IReadOnlyList<(PAction Action, string Label)> Plan => _plan;
    int _forecastMonths = 12;
    bool _forecastBusy;

    /// <summary>Sends an action, or adds it to the plan when planning is on. True when it went into the plan.</summary>
    public bool Act(PAction a, string label)
    {
        if (!Planning) { Send(a); return false; }
        _plan.Add((a, label));
        Toast?.Invoke($"Planned: {label}", "info");
        Changed();
        return true;
    }

    void RunForecast(bool withPlan)
    {
        _forecastBusy = true;
        var r = new ForecastRequest { Months = _forecastMonths };
        if (withPlan) r.Plan.AddRange(_plan.Select(p => p.Action));
        _send(new ClientMessage { Forecast = r });
    }

    // A side-by-side forecast for one ordinance: the city with and without it.
    sealed class Compare
    {
        public required string Id;
        public uint BaseSeq, PlanSeq;
        public ForecastResult? Base, Plan;
    }
    Compare? _compare;
    string _openLaw = "";

    // ---------------------------------------------------------------------------------------------------------
    // The guide

    string _article = "";
    /// <summary>What the text box above the panel is for, or null to hide it.</summary>
    public string? InputPrompt => Company.Active ? Company.InputPrompt : Dialog switch
    {
        "new" => "City name",
        "save" => "Save name",
        null when Tab == "guide" && _article == "" => "Search the guide…",
        _ => null,
    };
    /// <summary>What's in the text box; the view sets it as the player types.</summary>
    public string Input
    {
        get => Company.Active ? Company.Input : _input;
        private set => _input = value;
    }
    string _input = "";

    public void SetInput(string text)
    {
        if (Company.Active) { Company.SetInput(text); return; }
        if (text == Input) return;
        Input = text;
        Changed();
    }

    /// <summary>Shows a guide article.</summary>
    public void Article(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _article = id;
        Input = "";
        Dialog = null;
        Open("guide");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Following a commuter

    public void Follow(long id, string name, string job)
    {
        if (Roster == null || Roster.Trips.All(t => t.Id != id))
        {
            Toast?.Invoke($"{name} isn't drawn on the map right now. Only the first {Roster?.Trips.Count ?? 0:N0} followed commuters are.", "warning");
            return;
        }
        (Followed, FollowedName, FollowedJob) = (id, name, job);
        ShowAgents = true;
        Changed();
    }

    public void StopFollowing()
    {
        Followed = null;
        Changed();
    }

    public AgentTripInfo? FollowedTrip => Followed is { } id ? Roster?.Trips.FirstOrDefault(t => t.Id == id) : null;

    // ---------------------------------------------------------------------------------------------------------
    // Dialogs

    public void OpenDialog(string name)
    {
        // The dispatch board is the service's own screen now, not a dialog.
        if (name == "dispatch") { Click("em-open"); return; }
        Dialog = name;
        if (name == "seat" && Mayor is { } m) _seatDraft = (m.Level, m.Style, m.SpendCap);
        if (name == "load") _send(new ClientMessage { Save = new SaveRequest { Name = "" } });  // refresh the list
        if (name == "new") { Input = "New City"; _newSize = 128; _newStarter = _newBench = false; }
        if (name == "save")
        {
            var safe = new string((_city.Stats.CityName ?? "city").Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '_' or '-').ToArray());
            Input = safe.Length == 0 ? "city" : safe[..Math.Min(40, safe.Length)];
        }
        Changed();
    }

    public void CloseDialog()
    {
        Dialog = null;
        Changed();
    }

    (SeatLevel Level, AutopilotStyle Style, long Cap) _seatDraft;
    /// <summary>Which free district the seat dialog offers to put out to tender.</summary>
    int _tenderPick;
    int _newSize = 128;
    bool _newStarter, _newBench;
    // The lease terms dialog: what it's for, the terms being edited, and what OK does with them.
    (string Title, string Ok, string Note, LeaseTerms Terms, Action<LeaseTerms> Done)? _terms;
    (string Text, System.Action Yes)? _confirm;
    // The buyout or move being offered on a lease.
    (int LeaseId, OfferKind Kind, long Amount, int To, int CounterOf)? _offer;

    /// <summary>Whether this player speaks for the side game on a lease (its seat's owner); otherwise they speak for the city.</summary>
    bool SpeaksForOperator(LeaseState l) => Civic?.Seats.FirstOrDefault(s => s.Id == l.Game)?.Owner == Me();

    static OfferState? OpenOffer(LeaseState l) => l.Offers.FirstOrDefault(o => o.Status == "open");

    void SendOffer(LeaseState l, OfferKind kind, LeaseTerms? terms, long amount, int to, int counterOf) =>
        Send(new PAction { Offer = new OfferLease { LeaseId = l.Id, FromOperator = SpeaksForOperator(l), Kind = kind, Terms = terms, Amount = amount, ToBuildingId = to, CounterOf = counterOf } });

    /// <summary>The city's standard offer, as the sim defines it: a business lease, rent by size, a 10% fare tax, three months' notice.</summary>
    /// <summary>The terms the city offers anyone who asks, as the sim's <c>World.StandardTerms</c>: stations are public services.</summary>
    internal static LeaseTerms StandardTerms(string kind) => kind switch
    {
        "fire-station" or "police-station" => new() { Mode = LeaseMode.LeasePublicService, SubsidyPerMonth = 400, NoticeMonths = 3 },
        "clinic" => new() { Mode = LeaseMode.LeasePublicService, SubsidyPerMonth = 300, NoticeMonths = 3 },
        "hospital" => new() { Mode = LeaseMode.LeasePublicService, SubsidyPerMonth = 800, NoticeMonths = 3 },
        _ => new()
        {
            Mode = LeaseMode.LeaseBusiness, TaxTenths = 100, NoticeMonths = 3,
            RentPerMonth = kind switch { "bus-depot" => 300, "tram-depot" => 500, "rail-depot" => 1_200, "tram-stop" => 60, "rail-station" => 150, "freight-depot" => 400, _ => 40 },
        },
    };

    bool Allowed(Mechanic m) => Civic?.Consent?.Rows.FirstOrDefault(r => r.Mechanic == m)?.Effective ?? false;

    /// <summary>Hands the mayor's seat back to its owner.</summary>
    public void TakeBack()
    {
        var m = Mayor;
        Send(new PAction { Seat = new SetSeat { Seat = "mayor", Owner = string.IsNullOrEmpty(m?.Owner) ? Me() : m.Owner, Level = SeatLevel.SeatManual, Style = m?.Style ?? 0, SpendCap = m?.SpendCap ?? 25000 } });
    }

    // ---------------------------------------------------------------------------------------------------------
    // Links

    /// <summary>Runs a link from the panel: a meta string written by <see cref="Render"/>.</summary>
    public void Click(string meta)
    {
        // One side game's screen at a time.
        if (meta == "co-open" && Emergency.Active) Emergency.SetActive(false);
        if (meta == "em-open") { if (Company.Active) Company.SetActive(false); Dialog = null; }
        if (meta == "rg-open") { Company.SetActive(false); Emergency.SetActive(false); Dialog = null; }
        if (meta.StartsWith("rg-")) { Region.Click(meta); return; }
        if (meta.StartsWith("co-")) { Company.Click(meta); return; }
        if (meta.StartsWith("em-")) { Emergency.Click(meta); return; }
        var a = meta.Split(':');
        int I(int k) => k < a.Length && int.TryParse(a[k], out var v) ? v : -1;
        long L(int k) => k < a.Length && long.TryParse(a[k], out var v) ? v : 0;
        Rect? Area(int k) => a.Length >= k + 4 ? new Rect { X0 = I(k), Y0 = I(k + 1), X1 = I(k + 2), Y1 = I(k + 3) } : null;
        switch (a[0])
        {
            case "tab": Dialog = null; Open(a[1]); return;
            case "topic": Article(a[1]); return;
            case "guide-back": _article = ""; Changed(); return;
            case "jump":
                if (Dialog == "report") Dialog = null;
                JumpTo?.Invoke(I(1), I(2));
                Query(I(1), I(2));
                if (Area(3) is { X0: >= 0 } ar) Flash?.Invoke(ar);
                return;
            case "fix":
                PickBuilding?.Invoke(a[1], I(2), I(3));
                if (Area(4) is { X0: >= 0 } fa) Flash?.Invoke(fa);
                return;
            case "upgrade":
                Send(new PAction { Upgrade = new UpgradeBuilding { BuildingId = I(1), ToTypeId = a[2] } });
                Toast?.Invoke($"Upgrading to a {(_city.TypeById.TryGetValue(a[2], out var ut) ? ut.Name : a[2])}…", "info");
                Requery();
                return;
            case "fund":
                Send(new PAction { Funding = new SetFunding { BuildingId = I(1), Percent = Math.Clamp(I(2), 0, 200) } });
                Requery();
                return;
            case "tax":
                Send(new PAction { Tax = new SetTax { Zone = (ZoneKind)(I(1) / 3 + 1), Wealth = (Wealth)(I(1) % 3), PercentTenths = Math.Clamp(I(2), 0, 200) } });
                return;
            case "loan": Send(new PAction { Loan = new TakeLoan { Amount = L(1) } }); return;
            case "overlay":
                ActiveOverlay = I(1);
                if (ActiveOverlay != 0) RequestOverlay();
                else { Overlay = null; OverlayVersion++; }
                Changed();
                return;
            case "agents": ShowAgents = !ShowAgents; return;
            case "routes": ShowRoutes = !ShowRoutes; return;
            case "under": ShowUnderground = !ShowUnderground; return;
            case "follow":
                if (Followed == L(1)) StopFollowing();
                else Follow(L(1), a.Length > 2 ? Uri.UnescapeDataString(a[2]) : "Someone", a.Length > 3 ? Uri.UnescapeDataString(a[3]) : "");
                return;
            case "respond":
                Send(new PAction { Respond = new RespondPrompt { PromptId = I(1), Accept = I(2) == 1 } });
                return;

            // Laws
            case "law": _openLaw = _openLaw == a[1] ? "" : a[1]; Changed(); return;
            case "enact":
            {
                var act = new PAction { Ordinance = new SetOrdinance { Id = a[1], Enact = I(2) == 1 } };
                string name = Ordinances.FirstOrDefault(o => o.Id == a[1])?.Name ?? a[1];
                if (Act(act, $"{(I(2) == 1 ? "Enact" : "Repeal")} {name}")) return;
                Refresh("laws");
                return;
            }
            case "compare":
            {
                var o = Ordinances.FirstOrDefault(x => x.Id == a[1]);
                bool enacted = o?.Status == OrdinanceStatus.OrdinanceEnacted;
                var act = new PAction { Ordinance = new SetOrdinance { Id = a[1], Enact = !enacted } };
                // With an enacted ordinance, "with" is the city as it is and "without" repeals it.
                var without = new ForecastRequest { Months = 12 };
                var with = new ForecastRequest { Months = 12 };
                (enacted ? without : with).Plan.Add(act);
                _compare = new Compare { Id = a[1] };
                _compare.BaseSeq = _send(new ClientMessage { Forecast = without });
                _compare.PlanSeq = _send(new ClientMessage { Forecast = with });
                Changed();
                return;
            }

            // Forecast
            case "months": _forecastMonths = Math.Clamp(I(1), 1, 120); Changed(); return;
            case "forecast": RunForecast(false); Changed(); return;
            case "plan": Planning = !Planning; Changed(); return;
            case "plan-run": RunForecast(true); Changed(); return;
            case "plan-build":
                foreach (var p in _plan) Send(p.Action);
                _plan.Clear();
                Planning = false;
                Changed();
                return;
            case "plan-clear": _plan.Clear(); Changed(); return;

            // Who runs the city
            case "dialog": OpenDialog(a[1]); return;
            case "close": CloseDialog(); return;
            case "takeback": TakeBack(); return;
            case "level": _seatDraft.Level = (SeatLevel)I(1); Changed(); return;
            case "style": _seatDraft.Style = (AutopilotStyle)I(1); Changed(); return;
            case "cap": _seatDraft.Cap = Math.Max(0, _seatDraft.Cap + L(1)); Changed(); return;
            case "away": Send(new PAction { AwayLevel = new SetAwayLevel { Seat = "mayor", Level = (SeatLevel)I(1), Style = _seatDraft.Style } }); return;
            case "seat-apply":
            {
                var m = Mayor;
                Send(new PAction { Seat = new SetSeat { Seat = "mayor", Owner = string.IsNullOrEmpty(m?.Owner) ? Me() : m.Owner, Level = _seatDraft.Level, Style = _seatDraft.Style, SpendCap = _seatDraft.Cap } });
                CloseDialog();
                return;
            }
            case "invite": Send(new PAction { Attach = new AttachSideGame { Game = a[1] } }); return;
            case "tender-pick": _tenderPick += I(1); Changed(); return;
            case "tender": Send(new PAction { OpenTender = new OpenTender { District = I(1), Months = I(2) } }); return;
                        case "claim" or "leave":
            {
                bool claim = a[0] == "claim";
                Send(new PAction { Seat = new SetSeat { Seat = a[1], Owner = claim ? Me() : "", Level = claim ? SeatLevel.SeatManual : SeatLevel.SeatCaretaker, SpendCap = 25000 } });
                CloseDialog();
                return;
            }
            case "consent":
                Send(new PAction { Consent = new SetConsent { Owner = Me(), Mechanic = (Mechanic)I(1), Allowed = I(2) == 1 } });
                return;
            case "rule":
                Send(new PAction { CityRule = new SetCityRule { Mechanic = (Mechanic)I(1), Forbid = I(2) == 1 } });
                return;
            case "deputy":  // deputy:portfolios:player
                Send(new PAction { Deputy = new SetDeputy { Portfolios = (uint)I(1), Player = Uri.UnescapeDataString(a[2]) } });
                return;
            case "council":
                Send(new PAction { Council = new SetCouncil { On = I(1) == 1 } });
                return;
            case "vote":
                Send(new PAction { Vote = new Vote { Proposal = I(1), Yes = I(2) == 1 } });
                return;
            case "pact":  // pact:step:mechanic:with
                Send(new PAction { Pact = new Pact { Step = (Pact.Types.Step)I(1), Mechanic = (Mechanic)I(2), With = Uri.UnescapeDataString(a[3]) } });
                return;

            // Leases
            case "lease-to":
            {
                var g = Civic?.SideGames.FirstOrDefault(x => x.Id == a[1]);
                var b = _city.Buildings.GetValueOrDefault(I(2));
                if (g == null || b == null) return;
                int bid = b.Id;
                _terms = ($"Lease to {g.Name}", "Lease", "", StandardTerms(b.TypeId),
                    t => Send(new PAction { Lease = new LeaseLot { Game = g.Id, BuildingId = bid, Terms = t } }));
                Dialog = "terms";
                Changed();
                return;
            }
            case "lease-change":
            {
                var l = Civic?.Leases.FirstOrDefault(x => x.Id == I(1));
                if (l == null) return;
                int id = l.Id;
                _terms = ($"New terms for {l.GameName}", "Give notice",
                    $"The new terms start after {l.Terms.NoticeMonths} months' notice, so {l.GameName} isn't caught out.",
                    (l.PendingTerms ?? l.Terms).Clone(), t => Send(new PAction { LeaseTerms = new SetLeaseTerms { LeaseId = id, Terms = t } }));
                Dialog = "terms";
                Changed();
                return;
            }
            case "lease-end":
            {
                var l = Civic?.Leases.FirstOrDefault(x => x.Id == I(1));
                if (l == null) return;
                int id = l.Id;
                void End() => Send(new PAction { EndLease = new EndLease { LeaseId = id } });
                if (a.Length > 2 && a[2] == "evict")
                {
                    _confirm = ($"Evict {l.GameName}? It gets {l.Terms.NoticeMonths} months' notice, and the city pays it compensation when it goes.", End);
                    Dialog = "confirm";
                    Changed();
                }
                else End();
                return;
            }
            case "offer-make":  // offer-make:lease:kind, countering the open offer if there is one
            {
                var l = Civic?.Leases.FirstOrDefault(x => x.Id == I(1));
                if (l == null) return;
                var open = OpenOffer(l);
                var kind = (OfferKind)I(2);
                int counter = open?.Id ?? 0;
                if (kind == OfferKind.OfferTerms)
                {
                    _terms = ($"Offer terms to {(SpeaksForOperator(l) ? "the city" : l.GameName)}", "Offer", "If the other side accepts, the new terms start at once.",
                        (open?.Terms ?? l.Terms).Clone(), t => SendOffer(l, OfferKind.OfferTerms, t, 0, -1, counter));
                    Dialog = "terms";
                }
                else
                {
                    if (kind == OfferKind.OfferRelocate && l.MoveTo.Count == 0) return;
                    long amount = open?.Kind == kind ? open.Amount : kind == OfferKind.OfferRelocate ? 1000 : 0;
                    int to = open?.Kind == kind && open.ToBuildingId >= 0 ? open.ToBuildingId : l.MoveTo.FirstOrDefault()?.BuildingId ?? -1;
                    _offer = (l.Id, kind, amount, to, counter);
                    Dialog = "offer";
                }
                Changed();
                return;
            }
            case "offer" when _offer is { } of:
                switch (a[1])
                {
                    case "amount": _offer = of with { Amount = Math.Max(0, of.Amount + L(2)) }; break;
                    case "to": _offer = of with { To = I(2) }; break;
                    case "ok":
                        if (Civic?.Leases.FirstOrDefault(x => x.Id == of.LeaseId) is { } l)
                            SendOffer(l, of.Kind, null, of.Amount, of.Kind == OfferKind.OfferRelocate ? of.To : -1, of.CounterOf);
                        CloseDialog();
                        return;
                }
                Changed();
                return;
            case "delink":
            {
                string player = Uri.UnescapeDataString(a[1]);
                _confirm = (player == Me()
                    ? "Leave this city for good? You get a copy of it as it is now, with your seats and lots, to play on your own. Your leases here end and you can't rejoin for 90 days."
                    : $"De-link {player}? They take a copy of the city as it is now and leave. Their leases here end, what they owe is written off, and they can't come back for 90 days.",
                    () => Send(new PAction { Delink = new Delink { Player = player } }));
                Dialog = "confirm";
                Changed();
                return;
            }
            case "offer-answer":
                Send(new PAction { AnswerOffer = new AnswerOffer { OfferId = I(1), Accept = I(2) == 1 } });
                return;
            case "yes":
                _confirm?.Yes();
                CloseDialog();
                return;
            case "terms" when _terms is { } tm:
            {
                var t = tm.Terms;
                switch (a[1])
                {
                    case "mode": t.Mode = (LeaseMode)I(2); break;
                    case "rent": t.RentPerMonth = Math.Max(0, t.RentPerMonth + L(2)); break;
                    case "tax": t.TaxTenths = Math.Clamp(t.TaxTenths + I(2), 0, 500); break;
                    case "subsidy": t.SubsidyPerMonth = Math.Max(0, t.SubsidyPerMonth + L(2)); break;
                    case "farecap": t.FareCapCents = Math.Max(0, t.FareCapCents + I(2)); break;
                    case "notice": t.NoticeMonths = Math.Clamp(t.NoticeMonths + I(2), 1, 12); break;
                    case "ok":
                        var done = t.Clone();
                        if (done.Mode == LeaseMode.LeasePublicService) { done.RentPerMonth = 0; done.TaxTenths = 0; }
                        else { done.SubsidyPerMonth = 0; done.FareCapCents = 0; }
                        tm.Done(done);
                        CloseDialog();
                        return;
                }
                Changed();
                return;
            }

            // New, save and load
            case "size": _newSize = I(1); Changed(); return;
            case "starter": _newStarter = !_newStarter; Changed(); return;
            case "bench": _newBench = !_newBench; Changed(); return;
            case "new-ok":
                _send(new ClientMessage { NewCity = new NewCityRequest { Name = Input.Trim() is { Length: > 0 } n ? n : "New City", Size = _newSize, StarterTown = _newStarter, Benchmark = _newBench } });
                CloseDialog();
                return;
            case "save-ok":
                _send(new ClientMessage { Save = new SaveRequest { Name = Input.Trim() is { Length: > 0 } sn ? sn : "city" } });
                CloseDialog();
                return;
            case "load":
                _send(new ClientMessage { Load = new LoadRequest { Name = Uri.UnescapeDataString(a[1]) } });
                CloseDialog();
                return;
        }
    }

    /// <summary>Asks again about the selected cell a moment after an action, once the server has applied it.</summary>
    void Requery() => _requeryIn = 0.7;
    double _requeryIn = -1;

    /// <summary>Runs the panel's timers. Call once a frame.</summary>
    public void Tick(double dt)
    {
        Emergency.Tick(dt);
        if (_requeryIn < 0) return;
        _requeryIn -= dt;
        if (_requeryIn < 0 && Selected is { } s) Query(s.X, s.Y, false);
    }
}
