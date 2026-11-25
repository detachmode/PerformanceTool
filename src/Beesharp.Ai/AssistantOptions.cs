namespace Beesharp.Ai;

/// <summary>
/// Settings of the assistant, bound from the configuration section <c>AI</c>: environment variables <c>AI__Provider</c>, <c>AI__Endpoint</c>,
/// <c>AI__ApiKey</c>, <c>AI__Model</c>, <c>AI__SystemPrompt</c>, <c>AI__MaxToolCalls</c> (brain 0047).
/// </summary>
public sealed class AssistantOptions
{
    /// <summary>Configuration section name (<c>AI</c>) used when binding <see cref="AssistantOptions"/>.</summary>
    public const string SectionName = "AI";
    /// <summary>Identifier for the OpenAI and OpenAI-compatible provider (<c>OpenAI</c>).</summary>
    public const string OpenAiProvider = "OpenAI";

    /// <summary>Today only <c>OpenAI</c> (any OpenAI-compatible server through <see cref="Endpoint"/>).</summary>
    public string Provider { get; set; } = OpenAiProvider;
    /// <summary>Optional base URL of an OpenAI-compatible server (OpenAI, Azure's <c>/openai/v1</c>, Ollama <c>/v1</c>, LM Studio, vLLM, OpenRouter, ...). Empty = OpenAI.</summary>
    public string? Endpoint { get; set; }
    /// <summary>API key sent to the configured provider; optional when using local servers via <see cref="Endpoint"/>.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Model identifier passed to the chat client (required for the assistant to be enabled).</summary>
    public string? Model { get; set; }
    /// <summary>Replaces the built-in system prompt when set.</summary>
    public string? SystemPrompt { get; set; }
    /// <summary>Most tool calls the assistant may make while answering one message.</summary>
    public int MaxToolCalls { get; set; } = 24;
    /// <summary>Most conversations kept in memory (the least recently used one goes first).</summary>
    public int MaxSessions { get; set; } = 50;
    /// <summary>A conversation nobody wrote to for this many minutes is dropped.</summary>
    public int SessionIdleMinutes { get; set; } = 240;

    /// <summary>Why the assistant is not usable, or <see langword="null"/> when it is. Without a model there is no assistant.</summary>
    public string? NotConfiguredReason
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model)) return "No model is set (AI__Model).";
            if (!string.Equals(Provider, OpenAiProvider, StringComparison.OrdinalIgnoreCase))
                return $"Provider '{Provider}' is not supported; use '{OpenAiProvider}' (any OpenAI-compatible server).";
            if (string.IsNullOrWhiteSpace(ApiKey) && string.IsNullOrWhiteSpace(Endpoint))
                return "Set AI__ApiKey for OpenAI, or AI__Endpoint for an OpenAI-compatible server.";
            return null;
        }
    }

    /// <summary>Gets a value indicating whether the assistant has a valid configuration and can accept chat requests.</summary>
    public bool IsConfigured => NotConfiguredReason is null;
}
