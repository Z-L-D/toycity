using C = ToyCity.Contract;
using W = ToyCity.Protocol.Contract;

namespace ToyCity.Protocol;

/// <summary>
/// The side-game contract between its C# form (in-process side games) and its wire form (protocol/contract.proto).
/// Both directions for every message, so a side game hosted elsewhere sees exactly what an in-process one does.
/// </summary>
public static class ContractWire
{
    public static W.LeaseTerms ToWire(C.LeaseTerms t) => new()
    {
        Mode = (W.LeaseMode)(int)t.Mode, RentPerMonth = t.RentPerMonth, TaxTenths = t.TaxTenths, SubsidyPerMonth = t.SubsidyPerMonth,
        FareCapCents = t.FareCapCents, NoticeMonths = t.NoticeMonths,
    };

    public static C.LeaseTerms FromWire(W.LeaseTerms t) =>
        new((C.LeaseMode)(int)t.Mode, t.RentPerMonth, t.TaxTenths, t.SubsidyPerMonth, t.FareCapCents, t.NoticeMonths);

    public static W.Incident ToWire(C.Incident i) => new()
    {
        Id = i.Id, Kind = i.Kind, X = i.X, Y = i.Y, Source = i.Source, Cause = i.Cause, Severity = i.Severity, Text = i.Text,
    };

    public static C.Incident FromWire(W.Incident i) => new(i.Id, i.Kind, i.X, i.Y, i.Source, i.Cause, i.Severity, i.Text);

    public static W.CityToGame ToWire(C.CityToGame c)
    {
        var m = new W.CityToGame
        {
            Tick = c.Tick, Day = c.Day, Month = c.Month, Year = c.Year, NewMonth = c.NewMonth,
            Operator = (W.OperatorLevel)(int)c.Operator, OwnerName = c.OwnerName ?? "",
        };
        foreach (var l in c.Lots)
        {
            var w = new W.LeasedLot
            {
                LeaseId = l.LeaseId, BuildingId = l.BuildingId, Kind = l.Kind, X = l.X, Y = l.Y, W = l.W, H = l.H, AccessCell = l.AccessCell,
                DistrictId = l.DistrictId, Terms = ToWire(l.Terms), PendingMonths = l.PendingMonths, BadStanding = l.BadStanding,
            };
            if (l.PendingTerms != null) w.PendingTerms = ToWire(l.PendingTerms);
            m.Lots.Add(w);
        }
        foreach (var o in c.OpenLots)
            m.OpenLots.Add(new W.OpenLot { BuildingId = o.BuildingId, Kind = o.Kind, X = o.X, Y = o.Y, W = o.W, H = o.H, AccessCell = o.AccessCell, DistrictId = o.DistrictId, Terms = ToWire(o.Terms) });
        foreach (var d in c.Districts)
            m.Districts.Add(new W.District
            {
                Id = d.Id, Name = d.Name, X0 = d.X0, Y0 = d.Y0, X1 = d.X1, Y1 = d.Y1, Population = d.Population, Jobs = d.Jobs,
                LandValue = d.LandValue, Crime = d.Crime, Health = d.Health, Rating = d.Rating,
            });
        foreach (var t in c.Demand) m.Demand.Add(new W.TripDemand { FromDistrict = t.FromDistrict, ToDistrict = t.ToDistrict, Trips = t.Trips });
        foreach (var r in c.Ridership)
        {
            var w = new W.LineRidership { LineId = r.LineId, Riders = r.Riders };
            w.BoardingsByStop.AddRange(r.BoardingsByStop);
            m.Ridership.Add(w);
        }
        foreach (var i in c.Incidents) m.Incidents.Add(ToWire(i));
        foreach (var b in c.Bills) m.Bills.Add(new W.Bill { LeaseId = b.LeaseId, Label = b.Label, Amount = b.Amount });
        foreach (var o in c.Offers) m.Offers.Add(new W.PaidActionOffer { Kind = o.Kind, Name = o.Name, Cost = o.Cost, Allowed = o.Allowed, WhyNot = o.WhyNot });
        foreach (var r in c.AuthorityResults)
            m.AuthorityResults.Add(new W.AuthorityResult { Kind = r.Kind, DistrictId = r.DistrictId, Ok = r.Ok, Cost = r.Cost, Text = r.Text, Rating = r.Rating });
        return m;
    }

    public static C.CityToGame FromWire(W.CityToGame m)
    {
        var c = new C.CityToGame
        {
            Tick = m.Tick, Day = m.Day, Month = m.Month, Year = m.Year, NewMonth = m.NewMonth,
            Operator = (C.OperatorLevel)(int)m.Operator, OwnerName = m.OwnerName.Length == 0 ? null : m.OwnerName,
        };
        foreach (var l in m.Lots)
            c.Lots.Add(new C.LeasedLot(l.LeaseId, l.BuildingId, l.Kind, l.X, l.Y, l.W, l.H, l.AccessCell, l.DistrictId, FromWire(l.Terms),
                l.PendingTerms == null ? null : FromWire(l.PendingTerms), l.PendingMonths, l.BadStanding));
        foreach (var o in m.OpenLots) c.OpenLots.Add(new C.OpenLot(o.BuildingId, o.Kind, o.X, o.Y, o.W, o.H, o.AccessCell, o.DistrictId, FromWire(o.Terms)));
        foreach (var d in m.Districts)
            c.Districts.Add(new C.District(d.Id, d.Name, d.X0, d.Y0, d.X1, d.Y1, d.Population, d.Jobs, d.LandValue, d.Crime, d.Health, d.Rating));
        foreach (var t in m.Demand) c.Demand.Add(new C.TripDemand(t.FromDistrict, t.ToDistrict, t.Trips));
        foreach (var r in m.Ridership) c.Ridership.Add(new C.LineRidership(r.LineId, r.Riders, r.BoardingsByStop.ToArray()));
        foreach (var i in m.Incidents) c.Incidents.Add(FromWire(i));
        foreach (var b in m.Bills) c.Bills.Add(new C.Bill(b.LeaseId, b.Label, b.Amount));
        foreach (var o in m.Offers) c.Offers.Add(new C.PaidActionOffer(o.Kind, o.Name, o.Cost, o.Allowed, o.WhyNot));
        foreach (var r in m.AuthorityResults) c.AuthorityResults.Add(new C.AuthorityResult(r.Kind, r.DistrictId, r.Ok, r.Cost, r.Text, r.Rating));
        return c;
    }

    public static W.GameToCity ToWire(C.GameToCity g)
    {
        var m = new W.GameToCity { ServiceQuality = g.ServiceQuality, Status = g.Status };
        foreach (var l in g.Lines)
        {
            var w = new W.TransitLine { LineId = l.LineId, Name = l.Name, Color = l.Color, HeadwayMinutes = l.HeadwayMinutes, FareCents = l.FareCents, SeatsPerHour = l.SeatsPerHour };
            w.StopLeaseIds.AddRange(l.StopLeaseIds);
            m.Lines.Add(w);
        }
        foreach (var (k, v) in g.JobsByLease) m.JobsByLease[k] = v;
        foreach (var i in g.Incidents) m.Incidents.Add(ToWire(i));
        foreach (var (k, v) in g.Paid) m.Paid[k] = v;
        foreach (var (k, v) in g.RevenueByLease) m.RevenueByLease[k] = v;
        m.LeaseRequests.AddRange(g.LeaseRequests);
        m.LeaseReleases.AddRange(g.LeaseReleases);
        foreach (var a in g.AuthorityActions) m.AuthorityActions.Add(new W.AuthorityAction { Kind = a.Kind, DistrictId = a.DistrictId });
        return m;
    }

    public static C.GameToCity FromWire(W.GameToCity m)
    {
        var g = new C.GameToCity { ServiceQuality = m.ServiceQuality, Status = m.Status };
        foreach (var l in m.Lines) g.Lines.Add(new C.TransitLine(l.LineId, l.Name, l.Color, l.StopLeaseIds.ToArray(), l.HeadwayMinutes, l.FareCents, l.SeatsPerHour));
        foreach (var (k, v) in m.JobsByLease) g.JobsByLease[k] = v;
        foreach (var i in m.Incidents) g.Incidents.Add(FromWire(i));
        foreach (var (k, v) in m.Paid) g.Paid[k] = v;
        foreach (var (k, v) in m.RevenueByLease) g.RevenueByLease[k] = v;
        g.LeaseRequests.AddRange(m.LeaseRequests);
        g.LeaseReleases.AddRange(m.LeaseReleases);
        foreach (var a in m.AuthorityActions) g.AuthorityActions.Add(new C.AuthorityAction(a.Kind, a.DistrictId));
        return g;
    }

    public static W.Manifest ToWire(C.Manifest m)
    {
        var w = new W.Manifest { Id = m.Id, Name = m.Name, ContractVersion = m.ContractVersion, Description = m.Description };
        w.LotKinds.AddRange(m.LotKinds);
        return w;
    }

    public static C.Manifest FromWire(W.Manifest m) => new(m.Id, m.Name, m.ContractVersion, m.LotKinds.ToArray(), m.Description);

    public static W.StreetRoute ToWire(C.StreetRoute r)
    {
        var w = new W.StreetRoute { Minutes = r.Minutes };
        w.Cells.AddRange(r.Cells);
        return w;
    }

    public static C.StreetRoute FromWire(W.StreetRoute r) => new(r.Cells.ToArray(), r.Minutes);
}
