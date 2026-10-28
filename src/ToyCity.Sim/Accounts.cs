namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>
    /// Checks that the city's books balance: every count kept in two places agrees, and the people add up. Returns
    /// what doesn't, empty when all is well. A query: it changes nothing.
    /// </summary>
    public List<string> Accounts()
    {
        var bad = new List<string>();
        void Check(bool ok, string what) { if (!ok) bad.Add(what); }

        // People: homes, citizens and the headline population.
        long residents = 0, employed = 0, assigned = 0, citizens = 0, citizenWorkers = 0;
        var workersAt = new Dictionary<int, long>();
        foreach (var b in Buildings)
        {
            if (b == null) continue;
            if (b.Operating) residents += b.Residents;
            if (b.Residents == 0) continue;
            long sum = 0;
            foreach (var a in b.Assignments)
            {
                sum += a.Count;
                Check(a.Count > 0, $"home #{b.Id} has an empty assignment");
                Check(a.SiteId >= 0 && a.SiteId < Buildings.Count && Buildings[a.SiteId] != null, $"home #{b.Id} works at a missing site #{a.SiteId}");
                workersAt[a.SiteId] = workersAt.GetValueOrDefault(a.SiteId) + a.Count;
                Check(a.Stops.Length % 2 == 0, $"home #{b.Id} has a transit trip that gets on but never off");
                Check(a.ByTransit == (a.Stops.Length > 0), $"home #{b.Id} has stops without a ride, or a ride without stops");
            }
            Check(sum == b.Employed, $"home #{b.Id}: {b.Employed} employed but {sum} assigned");
            Check(b.Employed <= LaborOf(b), $"home #{b.Id}: {b.Employed} employed of {LaborOf(b)} workers");
            employed += b.Employed;
            assigned += sum;
            if (b.Operating)
            {
                var people = CitizensOf(b);
                citizens += people.Count;
                citizenWorkers += people.Count(c => c.Role == CitizenRole.Worker);
                Check(people.Count(c => c.Role is CitizenRole.Worker or CitizenRole.JobSeeker) == LaborOf(b), $"home #{b.Id}: citizens of working age don't match its labour force");
                Check(people.Select(c => c.Id).Distinct().Count() == people.Count, $"home #{b.Id}: two citizens share an id");
            }
        }
        Check(residents == Population, $"population {Population} but homes hold {residents}");
        Check(citizens == Population, $"population {Population} but {citizens} citizens");
        Check(assigned == employed, $"{employed} employed but {assigned} assigned");

        // Jobs: each site's workers are exactly the people assigned to it.
        foreach (var b in Buildings)
        {
            if (b == null || b.Jobs == 0) continue;
            long w = workersAt.GetValueOrDefault(b.Id);
            Check(b.Workers == w, $"site #{b.Id}: {b.Workers} workers but {w} assigned");
            Check(b.Workers <= b.Jobs, $"site #{b.Id}: {b.Workers} workers for {b.Jobs} jobs");
        }

        // Money: last month's budget lines add up to the income and expenses reported.
        long income = LastBudget.Where(l => l.Amount > 0).Sum(l => l.Amount);
        long expenses = -LastBudget.Where(l => l.Amount < 0).Sum(l => l.Amount);
        Check(income == LastIncome, $"budget lines give ${income:N0} income, reported ${LastIncome:N0}");
        Check(expenses == LastExpenses, $"budget lines give ${expenses:N0} expenses, reported ${LastExpenses:N0}");
        Check(Debt >= 0, "negative debt");
        return bad;
    }
}
