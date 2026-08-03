using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

/// <summary>
/// Normalizes model reasoning controls. Provider-supplied metadata always wins;
/// the provider specification table is only used when /models returns an ID alone.
/// </summary>
public static class AiModelCapabilityResolver
{
    public static AiModelCapability Normalize(string? providerId, AiModelCapability capability)
    {
        if (string.IsNullOrWhiteSpace(capability.ModelId)) return capability;
        if (capability.ReasoningEfforts.Count > 0)
        {
            capability.ReasoningParameter = string.IsNullOrWhiteSpace(capability.ReasoningParameter)
                ? DefaultParameter(providerId)
                : capability.ReasoningParameter;
            capability.Source = string.IsNullOrWhiteSpace(capability.Source)
                ? "api_metadata"
                : capability.Source;
            return capability;
        }

        var known = ResolveProviderSpecification(providerId, capability.ModelId);
        return known ?? capability;
    }

    private static string DefaultParameter(string? providerId) =>
        providerId?.Equals("anthropic", StringComparison.OrdinalIgnoreCase) == true
            ? "output_config.effort"
            : providerId?.Equals("openrouter", StringComparison.OrdinalIgnoreCase) == true
                ? "reasoning.effort"
                : "reasoning_effort";

    private static AiModelCapability? ResolveProviderSpecification(string? providerId, string modelId)
    {
        var provider = providerId?.Trim().ToLowerInvariant() ?? "";
        var model = modelId.Trim().ToLowerInvariant();
        return provider switch
        {
            "deepseek" => ResolveDeepSeek(modelId, model),
            "openai" => ResolveOpenAi(modelId, model),
            "anthropic" => ResolveAnthropic(modelId, model),
            "gemini" => ResolveGemini(modelId, model),
            "xai" => ResolveXai(modelId, model),
            "groq" => ResolveGroq(modelId, model),
            _ => null
        };
    }

    private static AiModelCapability? ResolveDeepSeek(string id, string model) =>
        model is "deepseek-v4-flash" or "deepseek-v4-pro"
            ? Known(id, ["low", "high", "max"], "reasoning_effort")
            : null;

    private static AiModelCapability? ResolveOpenAi(string id, string model)
    {
        if (StartsWithFamily(model, "gpt-5.6"))
            return Known(id, ["none", "low", "medium", "high", "xhigh", "max"], "reasoning_effort");
        if (StartsWithFamily(model, "gpt-5.5") && !model.Contains("-pro", StringComparison.Ordinal))
            return Known(id, ["none", "low", "medium", "high", "xhigh"], "reasoning_effort");
        if (StartsWithFamily(model, "gpt-5.4") && !model.Contains("-pro", StringComparison.Ordinal))
            return Known(id, ["none", "low", "medium", "high", "xhigh"], "reasoning_effort");
        if (StartsWithFamily(model, "gpt-5.2") || StartsWithFamily(model, "gpt-5.1"))
            return Known(id, ["none", "low", "medium", "high", "xhigh"], "reasoning_effort");
        if (model.Equals("gpt-5", StringComparison.Ordinal)
            || StartsWithFamily(model, "gpt-5-mini")
            || StartsWithFamily(model, "gpt-5-nano"))
            return Known(id, ["minimal", "low", "medium", "high"], "reasoning_effort");
        return null;
    }

    private static AiModelCapability? ResolveAnthropic(string id, string model)
    {
        if (model.Contains("claude-opus-5", StringComparison.Ordinal)
            || model.Contains("claude-sonnet-5", StringComparison.Ordinal)
            || model.Contains("claude-fable-5", StringComparison.Ordinal)
            || model.Contains("claude-mythos-5", StringComparison.Ordinal)
            || model.Contains("claude-opus-4-8", StringComparison.Ordinal)
            || model.Contains("claude-opus-4-7", StringComparison.Ordinal))
            return Known(id, ["low", "medium", "high", "xhigh", "max"], "output_config.effort");
        if (model.Contains("claude-opus-4-6", StringComparison.Ordinal)
            || model.Contains("claude-sonnet-4-6", StringComparison.Ordinal)
            || model.Contains("claude-mythos", StringComparison.Ordinal))
            return Known(id, ["low", "medium", "high", "max"], "output_config.effort");
        if (model.Contains("claude-opus-4-5", StringComparison.Ordinal))
            return Known(id, ["low", "medium", "high"], "output_config.effort");
        return null;
    }

    private static AiModelCapability? ResolveGemini(string id, string model)
    {
        if (model.Contains("gemini-3.1-pro", StringComparison.Ordinal)
            || model.Contains("gemini-2.5-pro", StringComparison.Ordinal))
            return Known(id, ["low", "medium", "high"], "reasoning_effort");
        if (model.Contains("gemini-3-pro", StringComparison.Ordinal))
            return Known(id, ["low", "high"], "reasoning_effort");
        if (model.Contains("gemini-3", StringComparison.Ordinal) && model.Contains("flash", StringComparison.Ordinal))
            return Known(id, ["minimal", "low", "medium", "high"], "reasoning_effort");
        if (model.Contains("gemini-2.5-flash", StringComparison.Ordinal))
            return Known(id, ["none", "low", "medium", "high"], "reasoning_effort");
        return null;
    }

    private static AiModelCapability? ResolveXai(string id, string model) =>
        model.Equals("grok-4.5", StringComparison.Ordinal)
            || model.StartsWith("grok-4.5-", StringComparison.Ordinal)
                ? Known(id, ["low", "medium", "high"], "reasoning_effort")
                : null;

    private static AiModelCapability? ResolveGroq(string id, string model)
    {
        if (model.Contains("gpt-oss-20b", StringComparison.Ordinal)
            || model.Contains("gpt-oss-120b", StringComparison.Ordinal))
            return Known(id, ["low", "medium", "high"], "reasoning_effort");
        if (model.Contains("qwen", StringComparison.Ordinal) && model.Contains("3", StringComparison.Ordinal))
            return Known(id, ["none"], "reasoning_effort");
        return null;
    }

    private static bool StartsWithFamily(string model, string family) =>
        model.Equals(family, StringComparison.Ordinal)
        || model.StartsWith(family + "-", StringComparison.Ordinal);

    private static AiModelCapability Known(string id, IReadOnlyList<string> efforts, string parameter) => new()
    {
        ModelId = id,
        ReasoningEfforts = AiReasoningEfforts.Ordered
            .Where(efforts.Contains)
            .ToList(),
        ReasoningParameter = parameter,
        Source = "provider_spec"
    };
}
