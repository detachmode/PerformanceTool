using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Client.Runs;

namespace Beesharp.Client.Data;

/// <summary>
/// Where the pages get their data (docs/brain/0015): the plan being edited, presets, run control and a snapshot of the
/// current run. <see cref="FakeRunSource"/> simulates everything in the browser (GitHub Pages demo);
/// <see cref="ApiRunSource"/> will talk to the server's REST API. Pick one with <c>DataSource</c> in
/// <c>wwwroot/appsettings.json</c>.
/// </summary>
public interface IRunSource : IDisposable
{
    /// <summary>Immutable view of the run. Replaced on every change; see <see cref="Changed"/>.</summary>
    RunSnapshot Current { get; }
    bool IsRunning { get; }

    /// <summary>Raised whenever <see cref="Current"/> changes, possibly off the UI thread. Pages must marshal with InvokeAsync.</summary>
    event Action? Changed;

    // The editable plan (what the editor changes) and its presets.
    LoadPlan Plan { get; }
    string PresetName { get; }
    IReadOnlyList<string> PresetNames { get; }
    /// <summary>True when the plan differs from the loaded or last saved preset.</summary>
    bool HasUnsavedChanges { get; }
    /// <summary>True when this scenario entry differs from the loaded or last saved preset (new or edited).</summary>
    bool IsEntryChanged(ScenarioRunConfig entry);
    void AddEntry(IScenario scenario);
    void RemoveEntry(int index);
    /// <summary>Saves the plan under <paramref name="name"/> and makes it the current preset.</summary>
    void SavePreset(string name);
    void LoadPreset(string name);

    // Run control.
    void Start();
    /// <summary>Pause (brain 0027): stop starting new bees and stop the run clock; running bees finish.</summary>
    void Pause();
    void Resume();
    void Stop();
}
