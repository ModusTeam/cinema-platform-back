using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Microsoft.AspNetCore.Identity;

namespace Cinema.Infrastructure.Services;

// API registration and both admin seed paths share the same all-or-none operation.
public sealed class CustomerRegistrationService(UserManager<User> users, CustomerFactsWriter facts)
{
    public Task<Result> CreateAsync(User user, string password, string role, CancellationToken ct = default)
    {
        if (user.Id == Guid.Empty) user.Id = Guid.NewGuid();
        return facts.ExecuteAsync(user.Id, async token =>
        {
            user.RecordRegistration(DateTime.UtcNow);
            // Identity saves internally; stage history before its first SaveChanges.
            await facts.StageAsync(user, Guid.NewGuid(), "Registered", token);
            IdentityResult created = await users.CreateAsync(user, password);
            if (!created.Succeeded) return Failure(created);
            IdentityResult assigned = await users.AddToRoleAsync(user, role);
            return assigned.Succeeded ? Result.Success() : Failure(assigned);
        }, ct);
    }

    private static Result Failure(IdentityResult result) => Result.Failure(new Error(
        "Identity.RegisterFailed", string.Join(", ", result.Errors.Select(error => error.Description))));
}
