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
}
