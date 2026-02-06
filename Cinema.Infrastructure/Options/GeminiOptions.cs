namespace Cinema.Infrastructure.Options;

public class GeminiOptions
{
    public const string SectionName = "Gemini";
    
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string EmbeddingModelId { get; set; } = "text-embedding-004";
}