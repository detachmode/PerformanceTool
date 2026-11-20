namespace PerformanceTool.Components.Drafts;

public enum SortKey { Plan, Name, P95, Errors, Rate, Instances }
public enum GroupKey { None, Type }

/// <summary>Open the details popup of one entry, optionally pre-filtered to a one-second time window (brain 0006).</summary>
public readonly record struct OpenRequest(int Index, double? WindowFrom = null);

/// <summary>
/// DRAFT: filter, sort and group state of the overview (brain 0006). Entries are identified by their index in the plan,
/// which is also what colours them, so filtering never repaints the remaining cards (brain 0002).
/// </summary>
public sealed class OverviewFilter
{
    public HashSet<string> Types { get; } = [];
    public HashSet<int> Hidden { get; } = [];
    public int? Only { get; private set; }
    public string Search { get; set; } = "";
    public bool ErrorsOnly { get; set; }
    public SortKey Sort { get; private set; } = SortKey.Plan;
    public bool Descending { get; private set; }
    public GroupKey Group { get; set; } = GroupKey.None;

    public bool Active => Types.Count > 0 || Hidden.Count > 0 || Only is not null || Search.Length > 0 || ErrorsOnly;

    public bool Passes(int index, string label, string type, string? description, int errors)
    {
        if (Only is { } only) return index == only;
        if (Hidden.Contains(index)) return false;
        if (Types.Count > 0 && !Types.Contains(type)) return false;
        if (ErrorsOnly && errors == 0) return false;
        if (Search.Length > 0
            && !label.Contains(Search, StringComparison.OrdinalIgnoreCase)
            && !type.Contains(Search, StringComparison.OrdinalIgnoreCase)
            && !(description?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)) return false;
        return true;
    }

    public void ToggleType(string type) { if (!Types.Remove(type)) Types.Add(type); }
    public void Hide(int index) { Only = null; Hidden.Add(index); }

    /// <summary>"Only this": drops every other filter and shows just this entry.</summary>
    public void Isolate(int index) { Clear(); Only = index; }
    public void ClearOnly() => Only = null;

    public void Clear() { Types.Clear(); Hidden.Clear(); Only = null; Search = ""; ErrorsOnly = false; }

    /// <summary>Picking the active key again flips the direction; a new key starts with its natural direction.</summary>
    public void SortBy(SortKey key)
    {
        if (key == Sort && key != SortKey.Plan) { Descending = !Descending; return; }
        Sort = key;
        Descending = key is not (SortKey.Plan or SortKey.Name);
    }

    public static string Title(SortKey k) => k switch
    {
        SortKey.Plan => "Plan order", SortKey.Name => "Name", SortKey.P95 => "p95",
        SortKey.Errors => "Errors", SortKey.Rate => "Rate", _ => Terms.InstancesCap,
    };
}
