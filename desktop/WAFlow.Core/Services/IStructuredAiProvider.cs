namespace WAFlow.Core.Services;

public interface IStructuredAiProvider
{
    bool HasApiKey();
    Task<string> GetSelectedModelAsync(CancellationToken cancellationToken = default);
    Task<T> CompleteStructuredAsync<T>(
        string instructions,
        object payload,
        Func<T, string?> validate,
        CancellationToken cancellationToken = default) where T : class;

    Task<string> ExtractImageTextAsync(
        string filePath,
        string mimeType,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前 AI Provider 未实现图片 OCR。");
}
