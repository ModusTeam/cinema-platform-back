using Microsoft.AspNetCore.Identity;

namespace Cinema.Domain.Entities;

public class User : IdentityUser<Guid>
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}