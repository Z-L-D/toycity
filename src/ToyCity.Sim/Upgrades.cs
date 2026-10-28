using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>An upgrade a building could take now, or what it still needs.</summary>
public sealed record UpgradeOption(UpgradeDef Def, BuildingType To, bool Available, string WhyNot);

/// <summary>
/// Civic buildings upgrade in place to a type their own type lists: cheaper than building the bigger type new, and on
/// offer only once the building has earned it (enough riders, a big enough city, the right neighbours). Where a type
/// lists more than one, the city picks a branch and the others are gone for that building.
/// </summary>
public sealed partial class World
{
    public List<UpgradeOption> UpgradeOptions(Building b)
    {
        var list = new List<UpgradeOption>();
        foreach (var u in TypeOf(b).Upgrades)
        {
            var to = Content.Types[Content.TypeIndex(u.To)];
            var missing = new List<string>();
            if (!b.Operating) missing.Add("it has to be working");
            if (u.Riders > 0 && b.Riders < u.Riders) missing.Add($"{u.Riders:N0} riders a day (it has {b.Riders:N0})");
            if (u.Population > 0 && Population < u.Population) missing.Add($"a city of {u.Population:N0} (it's {Population:N0})");
            if (u.NearZone.Length > 0 && !NearZone(b, u.NearZone)) missing.Add($"{ZoneWord(u.NearZone)} within 3 cells");
            list.Add(new UpgradeOption(u, to, missing.Count == 0, missing.Count == 0 ? "" : $"Needs {string.Join(", ", missing)}."));
        }
        return list;
    }

    static string ZoneWord(string z) => z switch { "R" => "homes", "C" => "shops or offices", _ => "industry" };

    bool NearZone(Building b, string z)
    {
        var zone = z switch { "R" => Zone.Residential, "C" => Zone.Commercial, _ => Zone.Industrial };
        foreach (var o in Buildings)
            if (o != null && o.Id != b.Id && !TypeOf(o).Placeable && TypeOf(o).Zone == zone && o.Operating && RectGap(b, o) <= 3) return true;
        return false;
    }

    void ApplyUpgrade(UpgradeBuildingAction a)
    {
        if (a.BuildingId < 0 || a.BuildingId >= Buildings.Count || Buildings[a.BuildingId] is not { } b) { Reject("That building isn't there any more."); return; }
        var opt = UpgradeOptions(b).FirstOrDefault(o => o.To.Id == a.ToTypeId);
        if (opt == null) { Reject($"A {TypeOf(b).Name} can't become a {a.ToTypeId}.", b.X, b.Y); return; }
        if (!opt.Available) { Reject($"Not yet: {opt.To.Name}. {opt.WhyNot}", b.X, b.Y); return; }
        if (!Spend(opt.Def.Cost, b.X, b.Y)) return;
        string from = TypeOf(b).Name;
        b.Type = opt.To.Index;
        InitTotals(b);
        var lease = LeaseOn(b.Id);
        if (lease != null) b.Jobs += lease.Jobs;
        Notices.Add(new SimNotice($"The {from} on {StreetNear(b.X, b.Y)} is now a {opt.To.Name}.", "good", b.X, b.Y));
        // A bigger lot costs more to lease: a business lease below the new standard rent moves up after notice.
        var std = StandardTermsFor(b);
        if (lease is { Terms.Mode: LeaseMode.Business } && lease.Terms.RentPerMonth < std.RentPerMonth && lease.PendingTerms == null)
        {
            lease.PendingTerms = lease.Terms with { RentPerMonth = std.RentPerMonth };
            lease.PendingMonths = lease.Terms.NoticeMonths;
            Notices.Add(new SimNotice($"{GameName(lease.GameId)}'s rent there goes up to ${std.RentPerMonth:N0} a month in {lease.PendingMonths} months, after notice.", "info", b.X, b.Y));
        }
    }
}
