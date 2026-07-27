namespace WAFlow.Core.Services;

public interface IStructuredAiProvider
{
    bool HasApiKey();
    bool HasApiKey(string moduleKey) => HasApiKey();
    Task<string> GetSelectedModelAsync(CancellationToken cancellationToken = default);
    Task<string> GetSelectedModelAsync(
        string moduleKey,
        CancellationToken cancellationToken = default) =>
        GetSelectedModelAsync(cancellationToken);
    Task<T> CompleteStructuredAsync<T>(
        string instructions,
        object payload,
        Func<T, string?> validate,
        CancellationToken cancellationToken = default) where T : class;
    Task<T> CompleteStructuredAsync<T>(
        string moduleKey,
        string instructions,
        object payload,
        Func<T, string?> validate,
        CancellationToken cancellationToken = default) where T : class =>
        CompleteStructuredAsync(instructions, payload, validate, cancellationToken);

    Task<string> ExtractImageTextAsync(
        string filePath,
        string mimeType,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前 AI Provider 未实现图片 OCR。");

    Task<string> ExtractImageTextAsync(
        string moduleKey,
        string filePath,
        string mimeType,
        CancellationToken cancellationToken = default) =>
        ExtractImageTextAsync(filePath, mimeType, cancellationToken);
}
