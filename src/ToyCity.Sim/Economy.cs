namespace ToyCity.Sim;

public sealed partial class World
{
    static readonly int[] Milestones = { 100, 500, 1_000, 2_500, 5_000, 10_000, 25_000, 50_000, 100_000 };

    /// <summary>Monthly tax base per resident (R) or job (C, I) at a 100% rate, by zone then wealth.</summary>
    static readonly int[] TaxBase = { 10, 16, 26, 12, 18, 28, 10, 14, 22 };

    void UpdateStats()
    {
        int pop = 0, pendingPop = 0, labor = 0, employed = 0;
        int jobsC = 0, jobsI = 0, jobsCivic = 0, liveJobs = 0;
        long eduWeighted = 0;
        foreach (var b in Buildings)
        {
            if (b == null || b.Abandoned) continue;
            var t = TypeOf(b);
            bool built = b.ConstructionLeft == 0;
            if (b.Residents > 0)
            {
                if (built)
                {
                    pop += b.Residents;
                    labor += LaborOf(b);
                    employed += b.Employed;
                    eduWeighted += (long)Coverage[(int)Service.Education][Idx(b.X, b.Y)] * b.Residents;
                }
                else pendingPop += b.Residents;
            }
            if (b.Jobs > 0)
            {
                if (built && b.Powered) liveJobs += b.Jobs;
                switch (t.Zone)
                {
                    case Zone.Commercial: jobsC += b.Jobs; break;
                    case Zone.Industrial: jobsI += b.Jobs; break;
                    default: jobsCivic += b.Jobs; break;
                }
            }
        }
        Population = pop;
        LaborForce = labor;
        Workers = employed;
        Jobs = liveJobs;
        EducationIndex = pop > 0 ? (int)(eduWeighted * 100 / 255 / pop) : 0;

        // Demand. Counts include buildings under construction so the city doesn't overshoot while they build.
        int p = pop + pendingPop;
        int l = p / 2;
        int jobs = jobsC + jobsI + jobsCivic;
        int r = 40 * 2000 / (2000 + p) + 60 * (jobs - l) / Math.Max(l, 100);
        int tc = p / 4;
        int c = 100 * (tc - jobsC) / Math.Max(tc, 40);
        int ti = 50 + l * 45 / 100;
        int ind = 100 * (ti - jobsI) / Math.Max(ti, 50);
        int edu = EducationIndex;
        int[] bias =
        {
            0, -5 + edu * 20 / 100, -20 + edu * 40 / 100,
            0, -5 + Math.Min(20, p / 500), -25 + Math.Min(40, p / 250),
            5 - edu * 30 / 100, 0, -40 + edu * 70 / 100,
        };
        int[] baseDemand = { r, c, ind };
        for (int k = 0; k < 9; k++)
        {
            int d = baseDemand[k / 3] + bias[k] - (TaxTenths[k] - 90) * 6 / 10;
            Demand[k] = Math.Clamp(d, -100, 100);
        }

        while (MilestoneIndex < Milestones.Length && pop >= Milestones[MilestoneIndex])
        {
            Notices.Add(new SimNotice($"{Name} reached a population of {Milestones[MilestoneIndex]:N0}!", "good"));
            MilestoneIndex++;
        }
    }

    static string BudgetLabel(Category c) => c switch
    {
        Category.Power => "Power plants",
        Category.Water => "Water pumps",
        Category.Garbage => "Garbage",
        Category.Police => "Police",
        Category.Fire => "Fire",
        Category.Health => "Health",
        Category.Education => "Education",
        Category.Park => "Parks",
        _ => "Other",
    };

    void RunBudget()
    {
        var taxNumerators = new long[3];
        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating) continue;
            var t = TypeOf(b);
            if (t.Placeable) continue;
            int k = ((int)t.Zone - 1) * 3 + (int)t.Wealth;
            int units = b.Residents > 0 ? b.Residents : Math.Min(b.Jobs, b.Workers);
            taxNumerators[(int)t.Zone - 1] += (long)units * TaxBase[k] * TaxTenths[k];
        }

        double pathCost = 0;
        int lines = 0, pipes = 0;
        for (int i = 0; i < N; i++)
        {
            if (Path[i] != 0) pathCost += Content.Profiles[Path[i] - 1].MaintenancePerCell;
            lines += PowerLine[i];
            pipes += WaterPipe[i];
        }

        var civic = new SortedDictionary<int, long>();
        foreach (var b in Buildings)
        {
            if (b == null) continue;
            var t = TypeOf(b);
            if (!t.Placeable) continue;
            civic.TryGetValue((int)t.Category, out long sum);
            civic[(int)t.Category] = sum + (long)t.Maintenance * b.Funding / 100;
        }

        LastBudget.Clear();
        long income = 0, expenses = 0;
        void Line(string label, long amount)
        {
            if (amount == 0) return;
            LastBudget.Add((label, amount));
            if (amount > 0) income += amount; else expenses -= amount;
        }
        Line("Residential taxes", taxNumerators[0] / 1000);
        Line("Commercial taxes", taxNumerators[1] / 1000);
        Line("Industrial taxes", taxNumerators[2] / 1000);
        Line("Path upkeep", -(long)Math.Round(pathCost));
        Line("Power lines and pipes", -(lines + pipes) / 10);
        foreach (var (cat, amount) in civic) Line(BudgetLabel((Category)cat), -amount);
        if (Debt > 0)
        {
            long interest = Debt / 200;
            long principal = Math.Min(Debt, Math.Max(1, Debt / 120));
            Debt -= principal;
            Line("Loan payments", -(interest + principal));
        }
        LastIncome = income;
        LastExpenses = expenses;
        bool wasPositive = Funds >= 0;
        Funds += income - expenses;
        if (Funds < 0 && wasPositive)
            Notices.Add(new SimNotice("The treasury is in the red. Raise taxes, cut funding, or take a loan.", "warning"));
    }
}
