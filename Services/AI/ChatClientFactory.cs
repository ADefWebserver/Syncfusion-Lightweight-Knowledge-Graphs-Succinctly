using Microsoft.Extensions.AI;
using OpenAI;

namespace SyncfusionHelpDesk.Services.AI;

/// <summary>
/// Builds an <see cref="IChatClient"/> over the OpenAI Responses API. OpenAI is
/// the only supported provider; there is no provider selector.
/// </summary>
public static class ChatClientFactory
{
    private const string DefaultModel = "gpt-4o-mini";

    // Substituted when no key is configured so construction does not throw at
    // start-up. The first real request then fails with a clear auth error.
    private const string PlaceholderApiKey = "not-configured";

    public static IChatClient Create(IConfiguration aiSection)
    {
        var apiKey = aiSection["ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = PlaceholderApiKey;
        }

        var model = aiSection["Model"];
        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        // Reasoning models (gpt-5 family) reject function tools on
        // /v1/chat/completions but accept them on /v1/responses, so the
        // Responses endpoint is required for this tool-calling assistant. The
        // Responses client is still experimental in the OpenAI SDK, hence the
        // OPENAI001 suppression.
#pragma warning disable OPENAI001
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
