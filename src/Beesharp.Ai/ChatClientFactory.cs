using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Beesharp.Ai;

/// <summary>Creates the model client for the configured provider. Tests replace it with a scripted fake <see cref="IChatClient"/>.</summary>
public interface IAssistantChatClientFactory
{
    /// <summary>Creates an <see cref="IChatClient"/> configured from <paramref name="options"/>.</summary>
    /// <param name="options">Assistant configuration containing provider, model, endpoint, and API key.</param>
    /// <returns>An <see cref="IChatClient"/> ready to process chat completions.</returns>
    IChatClient Create(AssistantOptions options);
}

/// <summary>OpenAI and every OpenAI-compatible server (chat completions), through Microsoft.Extensions.AI.OpenAI.</summary>
public sealed class OpenAiChatClientFactory : IAssistantChatClientFactory
{
    /// <summary>Creates an OpenAI-compatible <see cref="IChatClient"/> using <see cref="AssistantOptions.Endpoint"/>, <see cref="AssistantOptions.ApiKey"/>, and <see cref="AssistantOptions.Model"/>.</summary>
    /// <param name="options">Assistant configuration containing the model name and optional endpoint and API key.</param>
    /// <returns>An <see cref="IChatClient"/> backed by <see cref="OpenAIClient"/>.</returns>
    public IChatClient Create(AssistantOptions options)
    {
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint)) clientOptions.Endpoint = new Uri(options.Endpoint.Trim());
        // Local servers (Ollama, LM Studio) ignore the key but the client insists on one.
        var key = string.IsNullOrWhiteSpace(options.ApiKey) ? "not-needed" : options.ApiKey.Trim();
        var client = new OpenAIClient(new ApiKeyCredential(key), clientOptions);
        return client.GetChatClient(options.Model!.Trim()).AsIChatClient();
    }
}
