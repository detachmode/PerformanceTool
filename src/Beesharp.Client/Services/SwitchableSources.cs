using PerformanceTool.Services;
using PerformanceTool.Services.Environments;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Client.Services;

/// <summary>
/// Delegates <see cref="IRunSource"/> to <see cref="FakeRunSource"/> or <see cref="ApiRunSource"/> depending on
/// <see cref="DataModeState.IsFakeMode"/>, allowing instant toggling between Fake and Live mode in the UI.
/// </summary>
public sealed class SwitchableRunSource : IRunSource
{
    private readonly DataModeState mode;
    private readonly FakeRunSource fake;
    private readonly ApiRunSource api;
    private readonly ScriptApiClient scripts;

    public SwitchableRunSource(DataModeState mode, FakeRunSource fake, ApiRunSource api, ScriptApiClient scripts)
    {
        this.mode = mode;
        this.fake = fake;
        this.api = api;
        this.scripts = scripts;

        fake.Changed += OnFakeChanged;
        api.Changed += OnApiChanged;
        mode.Changed += OnModeChanged;
        if (!mode.IsFakeMode) api.StartSync();
    }

    private IRunSource Active => mode.IsFakeMode ? fake : api;

    public RunSnapshot Current => Active.Current;
    public bool IsRunning => Active.IsRunning;
    public event Action? Changed;

    public LoadPlan Plan => Active.Plan;
    public string PresetName => Active.PresetName;
    public IReadOnlyList<string> PresetNames => Active.PresetNames;
    public bool HasUnsavedChanges => Active.HasUnsavedChanges;
    public bool IsEntryChanged(ScenarioRunConfig entry) => Active.IsEntryChanged(entry);
    public void AddEntry(IScenario scenario) => Active.AddEntry(scenario);
    public void RemoveEntry(int index) => Active.RemoveEntry(index);
    public void SavePreset(string name) => Active.SavePreset(name);
    public void LoadPreset(string name) => Active.LoadPreset(name);
    public void Start() => Active.Start();
    public void Pause() => Active.Pause();
    public void Resume() => Active.Resume();
    public void Stop() => Active.Stop();

    private void OnFakeChanged() { if (mode.IsFakeMode) Changed?.Invoke(); }
    private void OnApiChanged() { if (!mode.IsFakeMode) Changed?.Invoke(); }

    private void OnModeChanged()
    {
        Changed?.Invoke();
        if (!mode.IsFakeMode)
        {
            _ = scripts.SyncCatalogAsync();
            _ = api.RefreshAsync();
            api.StartSync();
        }
        else api.StopSync();
    }

    public void Dispose()
    {
        fake.Changed -= OnFakeChanged;
        api.Changed -= OnApiChanged;
        mode.Changed -= OnModeChanged;
        fake.Dispose();
        api.Dispose();
    }
}

/// <summary>
/// Delegates <see cref="IEnvironmentSource"/> to <see cref="FakeEnvironmentSource"/> or <see cref="ApiEnvironmentSource"/>
/// depending on <see cref="DataModeState.IsFakeMode"/>.
/// </summary>
public sealed class SwitchableEnvironmentSource : IEnvironmentSource, IDisposable
{
    private readonly DataModeState mode;
    private readonly FakeEnvironmentSource fake;
    private readonly ApiEnvironmentSource api;

    public SwitchableEnvironmentSource(DataModeState mode, FakeEnvironmentSource fake, ApiEnvironmentSource api)
    {
        this.mode = mode;
        this.fake = fake;
        this.api = api;

        fake.Changed += OnFakeChanged;
        api.Changed += OnApiChanged;
        mode.Changed += OnModeChanged;
    }

    private IEnvironmentSource CurrentSource => mode.IsFakeMode ? fake : api;

    public IReadOnlyList<EnvironmentDefinition> Environments => CurrentSource.Environments;
    public string? ActiveName => CurrentSource.ActiveName;
    public EnvironmentDefinition? Active => CurrentSource.Active;
    public event Action? Changed;

    public void SetActive(string? name) => CurrentSource.SetActive(name);
    public void Save(string? originalName, EnvironmentDefinition environment) => CurrentSource.Save(originalName, environment);
    public void Delete(string name) => CurrentSource.Delete(name);

    private void OnFakeChanged() { if (mode.IsFakeMode) Changed?.Invoke(); }
    private void OnApiChanged() { if (!mode.IsFakeMode) Changed?.Invoke(); }

    private void OnModeChanged()
    {
        Changed?.Invoke();
        if (!mode.IsFakeMode) _ = api.RefreshAsync();
    }

    public void Dispose()
    {
        fake.Changed -= OnFakeChanged;
        api.Changed -= OnApiChanged;
        mode.Changed -= OnModeChanged;
    }
}
