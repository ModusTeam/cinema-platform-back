using System.Text.Json.Serialization;

namespace Cinema.Application.Common.Models.Gemini;

public class GeminiRequest
{
    public GeminiContent Content { get; set; } = new();
}

public class GeminiContent
{
    public List<GeminiPart> Parts { get; set; } = new();
}

public class GeminiPart
{
    public string Text { get; set; } = string.Empty;
}

public class GeminiResponse
{
    public GeminiEmbedding? Embedding { get; set; }
}

public class GeminiEmbedding
{
    public float[]? Values { get; set; }
}