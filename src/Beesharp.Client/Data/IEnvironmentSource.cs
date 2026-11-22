using Beesharp.Services.Environments;
using Beesharp.Client.Logs;

namespace Beesharp.Client.Data;

/// <summary>
/// Where the pages get the environments (brain 0033): the list, the active one and edits. <see cref="FakeEnvironmentSource"/> keeps them in
/// the browser; <see cref="ApiEnvironmentSource"/> will use the server's <c>/api/environments</c>. Pick with <c>DataSource</c>, like
/// <see cref="IRunSource"/>.
/// </summary>
public interface IEnvironmentSource
{
    /// <summary>All environments, in the order the user sees them.</summary>
    IReadOnlyList<EnvironmentDefinition> Environments { get; }
    /// <summary>Name of the active environment, or null for "No environment".</summary>
    string? ActiveName { get; }
    EnvironmentDefinition? Active { get; }
    event Action? Changed;

    void SetActive(string? name);
    /// <summary>Saves <paramref name="environment"/>. <paramref name="originalName"/> is the name it had before (null for a new one), so a rename replaces instead of adding.</summary>
    void Save(string? originalName, EnvironmentDefinition environment);
    void Delete(string name);
}
