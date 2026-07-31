using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace SyncfusionHelpDesk.Services.AI;

/// <summary>
/// Keeps OpenAI client construction behind <see cref="IChatClient"/>. OpenAI is
/// the only supported provider; there is no provider selector.
/// </summary>
public static class ChatClientFactory
{
    /// <summary>Model used when the <c>AI</c> section omits <c>Model</c>.</summary>
    public const string DefaultModel = "gpt-5.6-sol";

    /// <summary>Placeholder used so a missing key is not a startup failure.</summary>
    private const string PlaceholderApiKey = "missing-api-key";

    public static IChatClient Create(IConfiguration aiSection)
    {
        var apiKey = aiSection["ApiKey"];
        var model = string.IsNullOrWhiteSpace(aiSection["Model"])
            ? DefaultModel
            : aiSection["Model"]!;

        // A missing key must not throw at construction; the first real request
        // then fails with a clear authentication message the page surfaces.
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = PlaceholderApiKey;
        }

        // Reasoning models reject function tools on /v1/chat/completions but
        // accept them on /v1/responses, so the Responses endpoint is required.
#pragma warning disable OPENAI001 // Responses client is experimental in the OpenAI SDK.
        IChatClient chatClient = new OpenAIClient(apiKey)
            .GetResponsesClient()
            .AsIChatClient(model);
#pragma warning restore OPENAI001

        return chatClient
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }
}
