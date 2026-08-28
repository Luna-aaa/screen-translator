namespace ScreenTranslator.Translate;

public sealed class ModelListOutcome
{
    public bool IsSuccess { get; private init; }

    public IReadOnlyList<string> Models { get; private init; } = Array.Empty<string>();

    /// <summary>Plain-language result, shown to the user either way.</summary>
    public string Message { get; private init; } = "";

    public static ModelListOutcome Success(IReadOnlyList<string> models) => new()
    {
        IsSuccess = true,
        Models = models,
        Message = $"拉到 {models.Count} 个模型。",
    };

    public static ModelListOutcome Error(string message) => new() { Message = message };
}

/// <summary>
/// A translator whose service can be asked what models it offers. Separate from
/// <see cref="ITranslator"/> because not every backend has the notion — a traditional
/// translation API has no model list at all.
/// </summary>
public interface IModelCatalog
{
    Task<ModelListOutcome> ListModelsAsync(CancellationToken cancellationToken = default);
}
