namespace Cinema.Application.Common.Contracts;

public class TierUpgradedMessage
{
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty; 
    public string UserName { get; set; } = string.Empty;
    public string OldTier { get; set; } = string.Empty;
    public string NewTier { get; set; } = string.Empty;
    public DateTime UpgradedAt { get; set; }
}