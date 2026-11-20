using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Client.Services;

/// <summary>
/// Where the pages get their data (docs/brain/0015): the plan being edited, presets, run control and a snapshot of the
/// current run. <see cref="FakeRunSource"/> simulates everything in the browser (GitHub Pages demo, drafts);
/// <see cref="ApiRunSource"/> will talk to the server's REST API. Pick one with <c>DataSource</c> in
/// <c>wwwroot/appsettings.json</c>.
/// </summary>
public interface IRunSource : IDisposable
{
    /// <summary>Speeds offered by the demo speed switch.</summary>
    static readonly int[] DemoSpeeds = [1, 4, 10];

    /// <summary>Immutable view of the run. Replaced on every change; see <see cref="Changed"/>.</summary>
    RunSnapshot Current { get; }
    bool IsRunning { get; }

    /// <summary>Raised whenever <see cref="Current"/> changes, possibly off the UI thread. Pages must marshal with InvokeAsync.</summary>
    event Action? Changed;

    /// <summary>How much faster than real time a simulated run goes. Only meaningful when the source is a simulation.</summary>
    int DemoSpeed { get; set; }

    // The editable mix (what the editor changes).
    LoadPlan Plan { get; }
    string PresetName { get; set; }
    IReadOnlyList<string> PresetNames { get; }
    void AddEntry(IScenario scenario);
    void RemoveEntry(int index);
    void DuplicateEntry(int index);
    /// <summary>Moves an entry by <paramref name="delta"/> places (negative = up). Returns the new index.</summary>
    int MoveEntry(int index, int delta);
    void SavePreset(string name);
    void LoadPreset(string name);

    // Run control.
    void Start();
    void Stop();
}
