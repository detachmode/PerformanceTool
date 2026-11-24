using Beesharp.Client.Runs;

namespace Beesharp.Client.Logs;

/// <summary>
/// The lines of a stored run behind a filter, as one list of the full length that loads pages as they are looked at (brain 0021: runs can have hundreds of thousands
/// of lines). A row whose page is not here yet reads as <see cref="Placeholder"/>. Only a bounded number of pages is kept; the ones farthest from the window go first.
/// </summary>
public sealed class PagedLogList : IReadOnlyList<FakeLogEntry>
{
    public const int PageSize = 200;

    /// <summary>What a row shows while its page is on the way. One shared instance, so the list measures it once.</summary>
    public static readonly FakeLogEntry Placeholder = new(0, DateTimeOffset.MinValue, "Information", "", null, -1, -1, new Dictionary<string, string>());

    private readonly Dictionary<int, FakeLogEntry[]> pages = [];
    private readonly int maxPages;

    public PagedLogList(int maxPages = 60) => this.maxPages = Math.Max(4, maxPages);

    /// <summary>How many lines match the filter in all.</summary>
    public int Count { get; private set; }

    public int LoadedPages => pages.Count;

    public FakeLogEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return pages.TryGetValue(index / PageSize, out var rows) && index % PageSize < rows.Length ? rows[index % PageSize] : Placeholder;
        }
    }

    public IEnumerator<FakeLogEntry> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsLoaded(int page) => pages.ContainsKey(page);

    /// <summary>The number of lines changed (a run that goes on). The last page may have grown, so it is dropped to be loaded again; pages beyond the end go.</summary>
    public void SetTotal(long total)
    {
        var count = (int)Math.Min(int.MaxValue, Math.Max(0, total));
        if (count == Count) return;
        var lastOld = Count == 0 ? -1 : (Count - 1) / PageSize;
        Count = count;
        if (lastOld >= 0 && pages.TryGetValue(lastOld, out var rows) && rows.Length < PageSize) pages.Remove(lastOld);
        var lastNew = count == 0 ? -1 : (count - 1) / PageSize;
        foreach (var p in pages.Keys.Where(p => p > lastNew).ToList()) pages.Remove(p);
    }

    public void SetPage(int page, IReadOnlyList<FakeLogEntry> rows) => pages[page] = [.. rows];

    /// <summary>The pages that hold rows first to last (exclusive), widened by one page each side, that are not loaded yet; the one at <paramref name="first"/> first.</summary>
    public List<int> Missing(int first, int last)
    {
        var result = new List<int>();
        if (Count == 0) return result;
        var lastPage = (Count - 1) / PageSize;
        var from = Math.Clamp(first / PageSize - 1, 0, lastPage);
        var to = Math.Clamp((Math.Max(first, last - 1)) / PageSize + 1, 0, lastPage);
        var mid = Math.Clamp(first / PageSize, 0, lastPage);
        for (var p = from; p <= to; p++)
            if (!pages.ContainsKey(p)) result.Add(p);
        result.Sort((a, b) => Math.Abs(a - mid).CompareTo(Math.Abs(b - mid)));
        return result;
    }

    /// <summary>Drops the pages farthest from the window when more than the limit are held.</summary>
    public void Trim(int first, int last)
    {
        if (pages.Count <= maxPages) return;
        var mid = (first + last) / 2 / PageSize;
        foreach (var p in pages.Keys.OrderByDescending(p => Math.Abs(p - mid)).Take(pages.Count - maxPages).ToList()) pages.Remove(p);
    }
}
