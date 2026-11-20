using System.ComponentModel.DataAnnotations;

namespace PerformanceTool.Services.Scenarios.Spec;

/// <summary>Renders a <c>string</c> parameter as a textarea.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MultilineAttribute : Attribute;

/// <summary>
/// Renders a <c>string</c> parameter as a masked input. Secret values are not written to presets; a value of the
/// form <c>${env:NAME}</c> is kept and resolved from the environment when the run starts.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

/// <summary>
/// Validates that a <c>string</c> (or every item of a string list) is an absolute http(s) URL, and renders the
/// field as a URL input. Empty strings pass; combine with <see cref="RequiredAttribute"/> if needed.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class HttpUrlAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) => value switch
    {
        null => ValidationResult.Success,
        string s when s.Length == 0 || ScenarioContext.IsHttpUrl(s) => ValidationResult.Success,
        string => new ValidationResult($"{context.DisplayName} must be an absolute http(s) URL.", [context.MemberName!]),
        IEnumerable<string> items when items.All(ScenarioContext.IsHttpUrl) => ValidationResult.Success,
        IEnumerable<string> => new ValidationResult($"{context.DisplayName}: every entry must be an absolute http(s) URL.", [context.MemberName!]),
        _ => new ValidationResult($"{context.DisplayName}: [HttpUrl] only works on strings and string lists.", [context.MemberName!]),
    };
}
