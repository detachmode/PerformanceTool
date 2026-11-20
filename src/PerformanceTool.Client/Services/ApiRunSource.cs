using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Client.Services;

/// <summary>
/// Placeholder for the real data source (docs/brain/0015): it will call the server's REST API, the same one coding agents use.
/// It only exists to show the seam; every member throws until the API does.
/// </summary>
public sealed class ApiRunSource : IRunSource
{
    private static NotSupportedException NotYet() =>
        new("ApiRunSource is not implemented yet. Set \"DataSource\": \"Fake\" in wwwroot/appsettings.json.");

    public RunSnapshot Current => throw NotYet();
    public bool IsRunning => throw NotYet();
    public event Action? Changed { add { } remove { } }
    public int DemoSpeed { get => throw NotYet(); set => throw NotYet(); }
    public LoadPlan Plan => throw NotYet();
    public string PresetName { get => throw NotYet(); set => throw NotYet(); }
    public IReadOnlyList<string> PresetNames => throw NotYet();
    public void AddEntry(IScenario scenario) => throw NotYet();
    public void RemoveEntry(int index) => throw NotYet();
    public void DuplicateEntry(int index) => throw NotYet();
    public int MoveEntry(int index, int delta) => throw NotYet();
    public void SavePreset(string name) => throw NotYet();
    public void LoadPreset(string name) => throw NotYet();
    public void Start() => throw NotYet();
    public void Stop() => throw NotYet();
    public void Dispose() { }
}
