namespace Cinema.Application.Common.Settings;

public class ApplicationSettings
{
    public ConectionStrings? ConnectionStrings { get; set; }
}

public class ConectionStrings
{
    public string? DefaultConnection { get; set; }
}