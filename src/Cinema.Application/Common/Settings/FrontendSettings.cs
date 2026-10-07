namespace Cinema.Application.Common.Settings;

public class FrontendSettings
{
    public const string SectionName = "FrontendSettings";
    public string BaseUrl { get; set; } = string.Empty;
    public string TicketDownloadPath { get; set; } = "/tickets/{0}/download";
}