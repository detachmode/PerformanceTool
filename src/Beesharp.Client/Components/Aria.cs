namespace Beesharp.Client.Components;

/// <summary>Renders a bool as the explicit "true"/"false" that ARIA state attributes need (Blazor would drop or empty a bare bool).</summary>
public static class Aria
{
    public static string Bool(bool value) => value ? "true" : "false";
}
