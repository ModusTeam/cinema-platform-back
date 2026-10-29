using System.ComponentModel.DataAnnotations;

namespace Cinema.Catalog.Infrastructure.Options;

public sealed class CloudinaryOptions
{
    public const string SectionName = "Cloudinary";

    [Required, RegularExpression("^[a-zA-Z0-9_-]+$")]
    public string CloudName { get; init; } = string.Empty;

    [Required, RegularExpression("^[0-9]+$")]
    public string ApiKey { get; init; } = string.Empty;

    [Required]
    public string ApiSecret { get; init; } = string.Empty;
}
