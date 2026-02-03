namespace Cinema.Application.Common.Settings;

public class TmdbSettings
{
    public const string SectionName = "Tmdb";
    public string BaseUrl { get; set; } = "https://api.themoviedb.org"; 
    
    public string ApiKey { get; set; } = string.Empty;
    public string ImageBaseUrl { get; set; } = "https://image.tmdb.org/t/p/original";
}