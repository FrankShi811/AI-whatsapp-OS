namespace WAFlow.Core.Domain;

public sealed class WhatsAppConversationLanguageProfile
{
    public string ConversationId { get; set; } = "";
    public string LocalLanguageCode { get; set; } = "";
    public string LocalLanguageName { get; set; } = "";
    public string CustomerLanguageCode { get; set; } = "";
    public string CustomerLanguageName { get; set; } = "";
    public double Confidence { get; set; }
    public int SampleCount { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WhatsAppMessageTranslation
{
    public string MessageId { get; set; } = "";
    public string Direction { get; set; } = "";
    public string SourceLanguageCode { get; set; } = "";
    public string TargetLanguageCode { get; set; } = "";
    public string TargetLanguageName { get; set; } = "";
    public string SourceTextHash { get; set; } = "";
    public string OriginalText { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WhatsAppTranslationState
{
    public WhatsAppConversationLanguageProfile? Profile { get; set; }
    public List<WhatsAppMessageTranslation> Translations { get; set; } = [];
}

public sealed class WhatsAppLanguageDetectionResponse
{
    public string LanguageCode { get; set; } = "";
    public string LanguageName { get; set; } = "";
    public double Confidence { get; set; }
}

public sealed class WhatsAppTranslationBatchResponse
{
    public List<WhatsAppTranslationBatchItem> Items { get; set; } = [];
}

public sealed class WhatsAppTranslationBatchItem
{
    public string Id { get; set; } = "";
    public string SourceLanguageCode { get; set; } = "";
    public string TranslatedText { get; set; } = "";
}

public sealed class WhatsAppTranslationContext
{
    public WhatsAppConversationLanguageProfile Profile { get; set; } = new();
    public List<WhatsAppMessageTranslation> CachedTranslations { get; set; } = [];
}
