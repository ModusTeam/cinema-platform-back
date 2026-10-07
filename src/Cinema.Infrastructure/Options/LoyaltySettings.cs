namespace Cinema.Infrastructure.Options;

public class LoyaltySettings
{
    public const string SectionName = "InternalServices";

    public string ApiKey { get; init; } = string.Empty;
}
