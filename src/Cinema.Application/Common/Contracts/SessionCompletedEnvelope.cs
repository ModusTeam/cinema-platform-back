using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Cinema.Application.Common.Contracts;
public sealed record SessionCompletedData(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("completedAtUtc")] string CompletedAtUtc);
public sealed record SessionCompletedEnvelope(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("data")] SessionCompletedData Data)
{
    public static SessionCompletedEnvelope Create(Guid eventId, Guid sessionId, DateTime end)
    {
        if (end.Kind != DateTimeKind.Utc || end.Ticks % 10 != 0)
            throw new ArgumentException("Persisted UTC microsecond precision required");
        return new("SESSION_COMPLETED", new(1, eventId, sessionId, 1,
            end.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)));
    }
    public string ToJson() => JsonSerializer.Serialize(this);
}
