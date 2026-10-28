using System.Text.Json;

namespace ToyCity.Sim;

/// <summary>City-wide modifiers that rule graphs can set. Their sum over active ordinances feeds the systems.</summary>
public enum ModKey
{
    PowerUse, WaterUse, Garbage, IndustryPollution, Traffic, Attraction, Education, LandValue,
    DesirabilityR, DesirabilityC, DesirabilityI, DemandR, DemandC, DemandI, DemandDirty, DemandHighTech,
}

/// <summary>
/// One node of a rule graph. Nodes select buildings, measure them or the city, test numbers and act. A node reads
/// earlier nodes by index (or constants), so a graph is a list evaluated in order.
/// </summary>
public sealed class RuleNode
{
    public string Id = "";
    public string Op = "";
    public string Label = "";
    /// <summary>Inputs: an earlier node's index, or a constant when <see cref="InputIsConst"/> is set.</summary>
    public int[] Inputs = Array.Empty<int>();
    public long[] Consts = Array.Empty<long>();
    public bool[] InputIsConst = Array.Empty<bool>();
    public Dictionary<string, string> Params = new();
    public ModKey Key;
}

/// <summary>A compiled rule graph: the format every ordinance (and later every scenario) is written in.</summary>
public sealed class RuleGraph
{
    public List<RuleNode> Nodes { get; } = new();

    public static readonly Dictionary<string, ModKey> ModKeys = new()
    {
        ["power.use"] = ModKey.PowerUse, ["water.use"] = ModKey.WaterUse, ["garbage"] = ModKey.Garbage,
        ["pollution.industry"] = ModKey.IndustryPollution, ["traffic"] = ModKey.Traffic, ["attraction"] = ModKey.Attraction,
        ["education"] = ModKey.Education, ["landvalue"] = ModKey.LandValue,
        ["desirability.R"] = ModKey.DesirabilityR, ["desirability.C"] = ModKey.DesirabilityC, ["desirability.I"] = ModKey.DesirabilityI,
        ["demand.R"] = ModKey.DemandR, ["demand.C"] = ModKey.DemandC, ["demand.I"] = ModKey.DemandI,
        ["demand.I.dirty"] = ModKey.DemandDirty, ["demand.I.hightech"] = ModKey.DemandHighTech,
    };

    /// <summary>How a modifier reads in words, for previews and the guide.</summary>
    public static string DescribeMod(ModKey k, long v)
    {
        string s = v > 0 ? $"+{v}" : v.ToString();
        return k switch
        {
            ModKey.PowerUse => $"{s}% power use",
            ModKey.WaterUse => $"{s}% water use",
            ModKey.Garbage => $"{s}% garbage",
            ModKey.IndustryPollution => $"{s}% factory pollution",
            ModKey.Traffic => $"{s}% rush-hour traffic",
            ModKey.Attraction => $"{s} attraction",
            ModKey.Education => $"{s} education",
            ModKey.LandValue => $"{s} land value",
            ModKey.DesirabilityR => $"{s} residential desirability",
            ModKey.DesirabilityC => $"{s} commercial desirability",
            ModKey.DesirabilityI => $"{s} industrial desirability",
            ModKey.DemandR => $"{s} residential demand",
            ModKey.DemandC => $"{s} commercial demand",
            ModKey.DemandI => $"{s} industrial demand",
            ModKey.DemandDirty => $"{s} dirty industry demand",
            ModKey.DemandHighTech => $"{s} high-tech demand",
            _ => $"{s} {k}",
        };
    }

    static readonly HashSet<string> Ops = new()
    {
        "const", "select.buildings", "measure.count", "measure.sum", "measure.avg", "measure.stat",
        "math.add", "math.sub", "math.mul", "math.div", "math.min", "math.max", "math.percent", "math.neg",
        "test.gt", "test.ge", "test.lt", "test.le", "test.eq", "test.and", "test.or", "test.not",
        "act.modifier", "act.income", "act.expense",
    };

    public static readonly HashSet<string> Stats = new()
    {
        "population", "jobs", "workers", "laborForce", "unemployed", "funds", "education", "attraction", "month", "year",
        "powerCapacity", "powerDemand", "waterCapacity", "waterDemand", "garbageProduced", "garbageCapacity", "edgeCapacity", "buildings",
    };

    public static readonly HashSet<string> Fields = new() { "residents", "jobs", "workers", "powerKw", "waterUse", "garbage", "cells" };

    public static readonly HashSet<string> Layers = new()
    {
        "landValue", "pollution", "noise", "traffic", "desR", "desC", "desI", "police", "fire", "health", "education", "parks",
    };

    /// <summary>Compiles a JSON array of nodes. Inputs may be ids of earlier nodes, numbers, or inline node objects.</summary>
    public static RuleGraph Compile(JsonElement nodes, string owner)
    {
        var g = new RuleGraph();
        var ids = new Dictionary<string, int>();
        if (nodes.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{owner}: nodes must be an array");
        foreach (var n in nodes.EnumerateArray()) g.Add(n, ids, owner);
        return g;
    }

    int Add(JsonElement n, Dictionary<string, int> ids, string owner)
    {
        if (n.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{owner}: a node must be an object");
        var node = new RuleNode();
        var inputs = new List<int>();
        var consts = new List<long>();
        var isConst = new List<bool>();
        foreach (var p in n.EnumerateObject())
        {
            switch (p.Name)
            {
                case "id": node.Id = p.Value.GetString() ?? ""; break;
                case "op": node.Op = p.Value.GetString() ?? ""; break;
                case "label": node.Label = p.Value.GetString() ?? ""; break;
                case "in":
                    foreach (var v in p.Value.EnumerateArray())
                    {
                        switch (v.ValueKind)
                        {
                            case JsonValueKind.Number: inputs.Add(-1); consts.Add(v.GetInt64()); isConst.Add(true); break;
                            case JsonValueKind.String:
                                if (!ids.TryGetValue(v.GetString()!, out int idx))
                                    throw new InvalidDataException($"{owner}: node '{node.Id}' reads '{v.GetString()}', which is not an earlier node");
                                inputs.Add(idx); consts.Add(0); isConst.Add(false); break;
                            case JsonValueKind.Object: inputs.Add(Add(v, ids, owner)); consts.Add(0); isConst.Add(false); break;
                            default: throw new InvalidDataException($"{owner}: bad input in node '{node.Id}'");
                        }
                    }
                    break;
                default:
                    node.Params[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                    break;
            }
        }
        if (!Ops.Contains(node.Op)) throw new InvalidDataException($"{owner}: unknown op '{node.Op}'");
        node.Inputs = inputs.ToArray();
        node.Consts = consts.ToArray();
        node.InputIsConst = isConst.ToArray();
        int need = node.Op switch
        {
            "const" or "select.buildings" or "measure.stat" => 0,
            "measure.count" or "measure.sum" or "measure.avg" or "math.neg" or "test.not" => 1,
            "act.modifier" or "act.income" or "act.expense" => 1,
            _ => 2,
        };
        if (node.Inputs.Length < need) throw new InvalidDataException($"{owner}: {node.Op} needs {need} input(s)");
        if (node.Op == "act.modifier")
        {
            if (!node.Params.TryGetValue("key", out var key) || !ModKeys.TryGetValue(key, out node.Key))
                throw new InvalidDataException($"{owner}: act.modifier needs a known key");
        }
        if (node.Op == "measure.stat" && !Stats.Contains(node.Params.GetValueOrDefault("stat", "")))
            throw new InvalidDataException($"{owner}: unknown stat '{node.Params.GetValueOrDefault("stat")}'");
        if (node.Op == "measure.sum" && !Fields.Contains(node.Params.GetValueOrDefault("field", "")))
            throw new InvalidDataException($"{owner}: unknown field '{node.Params.GetValueOrDefault("field")}'");
        if (node.Op == "measure.avg" && !Layers.Contains(node.Params.GetValueOrDefault("layer", "")))
            throw new InvalidDataException($"{owner}: unknown layer '{node.Params.GetValueOrDefault("layer")}'");
        if (node.Op == "const" && !node.Params.ContainsKey("value")) throw new InvalidDataException($"{owner}: const needs a value");
        int index = Nodes.Count;
        Nodes.Add(node);
        if (node.Id.Length > 0)
        {
            if (!ids.TryAdd(node.Id, index)) throw new InvalidDataException($"{owner}: duplicate node id '{node.Id}'");
        }
        return index;
    }
}

/// <summary>A value flowing through a rule graph: a number, or a selection of building ids.</summary>
public readonly record struct RuleValue(long Number, List<int>? Selection)
{
    public bool IsSelection => Selection != null;
    public override string ToString() => Selection != null ? $"{Selection.Count:N0} buildings" : Number.ToString("N0");
}

/// <summary>What one run of a graph decided: modifiers, money, and (for previews) each step in words.</summary>
public sealed class RuleOutcome
{
    public readonly long[] Mods = new long[Enum.GetValues<ModKey>().Length];
    public readonly List<(string Label, long Amount)> Money = new();
    public readonly List<(string Label, string Value)> Trace = new();
    public RuleValue Last;
}

public sealed partial class World
{
    /// <summary>
    /// Runs a rule graph against the city. Pure: it reads the state and returns what the graph would do, so the
    /// same call serves the monthly update, previews and the guide.
    /// </summary>
    public RuleOutcome EvaluateRules(RuleGraph g, bool trace = false)
    {
        var o = new RuleOutcome();
        var vals = new RuleValue[g.Nodes.Count];
        for (int k = 0; k < g.Nodes.Count; k++)
        {
            var n = g.Nodes[k];
            long In(int j) => n.InputIsConst[j] ? n.Consts[j] : vals[n.Inputs[j]].IsSelection ? vals[n.Inputs[j]].Selection!.Count : vals[n.Inputs[j]].Number;
            List<int> Sel(int j) => n.InputIsConst[j] ? new List<int>() : vals[n.Inputs[j]].Selection ?? new List<int>();
            bool When(int j) => n.Inputs.Length <= j || In(j) != 0;
            RuleValue v = default;
            switch (n.Op)
            {
                case "const": v = new RuleValue(long.Parse(n.Params["value"]), null); break;
                case "select.buildings": v = new RuleValue(0, SelectBuildings(n.Params)); break;
                case "measure.count": v = new RuleValue(Sel(0).Count, null); break;
                case "measure.sum": v = new RuleValue(Sel(0).Sum(id => FieldOf(Buildings[id]!, n.Params["field"])), null); break;
                case "measure.avg":
                {
                    var s = Sel(0);
                    v = new RuleValue(s.Count == 0 ? 0 : s.Sum(id => LayerAt(n.Params["layer"], Idx(Buildings[id]!.X, Buildings[id]!.Y))) / s.Count, null);
                    break;
                }
                case "measure.stat": v = new RuleValue(StatOf(n.Params["stat"]), null); break;
                case "math.add": v = new RuleValue(In(0) + In(1), null); break;
                case "math.sub": v = new RuleValue(In(0) - In(1), null); break;
                case "math.mul": v = new RuleValue(In(0) * In(1), null); break;
                case "math.div": v = new RuleValue(In(1) == 0 ? 0 : In(0) / In(1), null); break;
                case "math.min": v = new RuleValue(Math.Min(In(0), In(1)), null); break;
                case "math.max": v = new RuleValue(Math.Max(In(0), In(1)), null); break;
                case "math.percent": v = new RuleValue(In(0) * In(1) / 100, null); break;
                case "math.neg": v = new RuleValue(-In(0), null); break;
                case "test.gt": v = new RuleValue(In(0) > In(1) ? 1 : 0, null); break;
                case "test.ge": v = new RuleValue(In(0) >= In(1) ? 1 : 0, null); break;
                case "test.lt": v = new RuleValue(In(0) < In(1) ? 1 : 0, null); break;
                case "test.le": v = new RuleValue(In(0) <= In(1) ? 1 : 0, null); break;
                case "test.eq": v = new RuleValue(In(0) == In(1) ? 1 : 0, null); break;
                case "test.and": v = new RuleValue(In(0) != 0 && In(1) != 0 ? 1 : 0, null); break;
                case "test.or": v = new RuleValue(In(0) != 0 || In(1) != 0 ? 1 : 0, null); break;
                case "test.not": v = new RuleValue(In(0) == 0 ? 1 : 0, null); break;
                case "act.modifier":
                {
                    bool on = When(1);
                    long amount = In(0);
                    if (on) o.Mods[(int)n.Key] += amount;
                    if (trace) o.Trace.Add((on ? "Effect" : "Effect (not now)", RuleGraph.DescribeMod(n.Key, amount)));
                    v = new RuleValue(on ? amount : 0, null);
                    break;
                }
                case "act.income":
                case "act.expense":
                {
                    bool on = When(1);
                    long amount = Math.Max(0, In(0)) * (n.Op == "act.income" ? 1 : -1);
                    string label = n.Label.Length > 0 ? n.Label : n.Op == "act.income" ? "Income" : "Cost";
                    if (on && amount != 0) o.Money.Add((label, amount));
                    if (trace) o.Trace.Add((on ? (amount >= 0 ? "Income" : "Cost") : "Not charged now", $"{label}: {(amount < 0 ? "-" : "")}${Math.Abs(amount):N0} a month"));
                    v = new RuleValue(on ? amount : 0, null);
                    break;
                }
            }
            vals[k] = v;
            if (trace && !n.Op.StartsWith("act.") && n.Label.Length > 0)
                o.Trace.Add((n.Label, n.Op.StartsWith("test.") ? (v.Number != 0 ? "yes" : "no") : v.ToString()));
        }
        if (g.Nodes.Count > 0) o.Last = vals[^1];
        return o;
    }

    List<int> SelectBuildings(Dictionary<string, string> p)
    {
        var list = new List<int>();
        p.TryGetValue("zone", out var zone);
        p.TryGetValue("density", out var density);
        p.TryGetValue("wealth", out var wealth);
        p.TryGetValue("category", out var category);
        p.TryGetValue("type", out var type);
        bool zonedOnly = p.GetValueOrDefault("zoned") == "true";
        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating) continue;
            var t = TypeOf(b);
            if (zonedOnly && t.Placeable) continue;
            if (zone != null && (t.Placeable || "RCI"[(int)t.Zone - 1].ToString() != zone)) continue;
            if (density != null && (t.Placeable || "LMH"[(int)t.Density].ToString() != density)) continue;
            if (wealth != null && (t.Placeable || "LMH"[(int)t.Wealth].ToString() != wealth)) continue;
            if (category != null && !string.Equals(t.Category.ToString(), category, StringComparison.OrdinalIgnoreCase)) continue;
            if (type != null && t.Id != type) continue;
            list.Add(b.Id);
        }
        return list;
    }

    static long FieldOf(Building b, string field) => field switch
    {
        "residents" => b.Residents, "jobs" => b.Jobs, "workers" => b.Workers, "powerKw" => b.PowerKw,
        "waterUse" => b.WaterUse, "garbage" => b.Garbage, "cells" => b.W * b.H, _ => 0,
    };

    long LayerAt(string layer, int i) => layer switch
    {
        "landValue" => LandValue[i], "pollution" => Pollution[i], "noise" => Noise[i], "traffic" => Traffic[i],
        "desR" => DesR[i], "desC" => DesC[i], "desI" => DesI[i],
        "police" => Coverage[(int)Service.Police][i] * 100 / 255, "fire" => Coverage[(int)Service.Fire][i] * 100 / 255,
        "health" => Coverage[(int)Service.Health][i] * 100 / 255, "education" => Coverage[(int)Service.Education][i] * 100 / 255,
        "parks" => Coverage[(int)Service.Parks][i] * 100 / 255, _ => 0,
    };

    long StatOf(string stat) => stat switch
    {
        "population" => Population, "jobs" => Jobs, "workers" => Workers, "laborForce" => LaborForce,
        "unemployed" => Math.Max(0, LaborForce - Workers), "funds" => Funds, "education" => EducationIndex,
        "attraction" => Attraction, "month" => Month, "year" => Year, "powerCapacity" => PowerCapacity,
        "powerDemand" => PowerDemand, "waterCapacity" => WaterCapacity, "waterDemand" => WaterDemand,
        "garbageProduced" => GarbageProduced, "garbageCapacity" => GarbageCapacity, "edgeCapacity" => EdgeCapacity,
        "buildings" => Buildings.Count(b => b != null), _ => 0,
    };
}
