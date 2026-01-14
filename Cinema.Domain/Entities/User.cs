using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class User : BaseEntity
{
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string Role { get; set; } = "Client";
    public string FirstName { get; set; }
    public string LastName { get; set; }
    
    
}