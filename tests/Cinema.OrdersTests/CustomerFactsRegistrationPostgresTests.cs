using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Services;
using Cinema.Infrastructure.Seed;
using FluentAssertions;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public sealed partial class CustomerFactsPostgresTests
{
    private static IdentityService Registration(AsyncServiceScope scope) => ActivatorUtilities.CreateInstance<IdentityService>(
        scope.ServiceProvider, Substitute.For<ITokenService>());

    [Fact]
    public async Task Registration_Commits_Required_Role_Source_History_And_Outbox_While_Broker_Is_Down()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id;
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            Result<Guid> result = await Registration(scope).RegisterAsync("registered@example.invalid", "Proof1!password", "Test", "User");
            result.IsSuccess.Should().BeTrue();
            id = result.Value;
        }
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        User user = await db.Users.SingleAsync(Cancellation);
        user.Id.Should().Be(id);
        user.CustomerFactsRevision.Should().Be(1);
        user.RegisteredAtUtc.Should().NotBeNull();
        user.DobFirstSetAtUtc.Should().BeNull();
        CustomerFactRevision fact = await db.CustomerFactRevisions.SingleAsync(Cancellation);
        await AssertRegisteredSourceAsync(db, fact, user);
        (await db.Set<IdentityUserRole<Guid>>().CountAsync(Cancellation)).Should().Be(1);
        database.Saves.Observed.Where(x => x.Context is not null).Select(x => x.TransactionId)
            .Should().OnlyContain(id => id.HasValue);
    }

    private static async Task AssertRegisteredSourceAsync(ApplicationDbContext db, CustomerFactRevision fact, User user)
    {
        fact.UserId.Should().Be(user.Id);
        fact.Revision.Should().Be(1);
        var original = Cinema.Application.Common.Contracts.CustomerFactsEnvelope.FromJson(fact.Snapshot);
        original.Data.RegisteredAtUtc.Should().Be(original.Data.OccurredAtUtc);
        var delivered = Cinema.Application.Common.Contracts.CustomerFactsEnvelope.FromJson(
            (await db.Set<OutboxMessage>().SingleAsync(Cancellation)).Body);
        delivered.Should().Be(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_Rejected_Create_Or_Role_Result_Leaves_No_State(bool roleFailure)
    {
        await using ProofDatabase database = await CreateDatabaseAsync(configureServices: services =>
        {
            if (roleFailure) services.AddScoped<IUserValidator<User>, RejectSecondUserValidation>();
        });
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            Result<Guid> result = await Registration(scope).RegisterAsync("rejected@example.invalid",
                roleFailure ? "Proof1!password" : "bad", "Test", "User");
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("Identity.RegisterFailed");
        }
        await AssertNoRegistrationAsync(database);
    }

    [Theory]
    [InlineData("outbox_message")]
    [InlineData("AspNetUserRoles")]
    public async Task Registration_Database_Failure_After_Intermediate_Save_Rolls_Back_Everything(string table)
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        await using (AsyncServiceScope setup = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = AssertSharedContext(setup);
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_registration() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected registration failure'; END $$", Cancellation);
            // Table is an InlineData allowlist, never request data.
            string trigger = table == "outbox_message"
                ? "CREATE TRIGGER reject_registration BEFORE INSERT ON outbox_message FOR EACH ROW EXECUTE FUNCTION reject_registration()"
                : "CREATE TRIGGER reject_registration BEFORE INSERT ON \"AspNetUserRoles\" FOR EACH ROW EXECUTE FUNCTION reject_registration()";
            await db.Database.ExecuteSqlRawAsync(trigger, Cancellation);
        }
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            Func<Task> register = () => Registration(scope).RegisterAsync("fault@example.invalid", "Proof1!password", "Test", "User");
            await register.Should().ThrowAsync<DbUpdateException>();
        }
        await AssertNoRegistrationAsync(database);
    }

    [Fact]
    public async Task Registration_Duplicate_And_Concurrent_Requests_Create_Only_One_Fact()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        await using AsyncServiceScope first = database.Services.CreateAsyncScope();
        await using AsyncServiceScope second = database.Services.CreateAsyncScope();
        Task<Result<Guid>> a = Registration(first).RegisterAsync("duplicate@example.invalid", "Proof1!password", "A", "User");
        Task<Result<Guid>> b = Registration(second).RegisterAsync("duplicate@example.invalid", "Proof1!password", "B", "User");
        // Unique normalized username arbitrates overlapping registrations; no business effect may duplicate.
        foreach (Task<Result<Guid>> pending in new[] { a, b })
        {
            try { await pending; }
            catch (DbUpdateException) { }
        }
        await using AsyncServiceScope retry = database.Services.CreateAsyncScope();
        (await Registration(retry).RegisterAsync("duplicate@example.invalid", "Proof1!password", "Retry", "User")).IsFailure.Should().BeTrue();
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        (await db.Users.CountAsync(Cancellation)).Should().Be(1);
        (await db.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(1);
        (await db.Set<OutboxMessage>().CountAsync(Cancellation)).Should().Be(1);
    }

    [Fact]
    public async Task Registration_Lost_Commit_Response_Does_Not_Replay_And_Repeat_Does_Not_Create_Another_Fact()
    {
        LostCommitAcknowledgement fault = new();
        await using ProofDatabase database = await CreateDatabaseAsync(fault);
        fault.Armed = true;
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            Func<Task> register = () => Registration(scope).RegisterAsync("lost@example.invalid", "Proof1!password", "Test", "User");
            await register.Should().ThrowAsync<Npgsql.NpgsqlException>();
        }
        fault.Failures.Should().Be(1);
        await using AsyncServiceScope repeat = database.Services.CreateAsyncScope();
        (await Registration(repeat).RegisterAsync("lost@example.invalid", "Proof1!password", "Test", "User")).IsFailure.Should().BeTrue();
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        (await db.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(1);
        (await db.Set<OutboxMessage>().CountAsync(Cancellation)).Should().Be(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Admin_Seed_New_User_Has_One_Registered_Fact_And_Rerun_Does_Not_Invent_History(bool legacy, bool initializer)
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        string email = initializer ? "admin@cinema.com" : "admin@example.invalid";
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SeedAdmin:Email"] = email, ["SeedAdmin:Password"] = "Proof1!password"
        }).Build();
        if (legacy)
        {
            await using AsyncServiceScope seed = database.Services.CreateAsyncScope();
            User user = NewUser(Guid.NewGuid()); user.Email = user.UserName = email;
            (await seed.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, "Proof1!password")).Succeeded.Should().BeTrue();
            if (initializer)
            {
                // The development initializer preserves existing admins; unlike
                // IdentitySeeder it does not repair an existing user's roles.
                (await seed.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>()
                    .CreateAsync(new IdentityRole<Guid>("Admin"))).Succeeded.Should().BeTrue();
                (await seed.ServiceProvider.GetRequiredService<UserManager<User>>()
                    .AddToRoleAsync(user, "Admin")).Succeeded.Should().BeTrue();
            }
        }
        for (int i = 0; i < 2; i++)
        {
            await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
            if (initializer)
                await ActivatorUtilities.CreateInstance<ApplicationDbContextInitializer>(scope.ServiceProvider,
                    NullLogger<ApplicationDbContextInitializer>.Instance).TrySeedAsync();
            else
                await IdentitySeeder.SeedAsync(scope.ServiceProvider, config, NullLogger.Instance);
        }
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        User admin = await db.Users.SingleAsync(Cancellation);
        admin.CustomerFactsRevision.Should().Be(legacy ? 0 : 1);
        admin.RegisteredAtUtc.HasValue.Should().Be(!legacy);
        (await db.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(legacy ? 0 : 1);
        (await verify.ServiceProvider.GetRequiredService<UserManager<User>>().IsInRoleAsync(admin, "Admin")).Should().BeTrue();
    }

    private static async Task AssertNoRegistrationAsync(ProofDatabase database)
    {
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        (await db.Users.CountAsync(Cancellation)).Should().Be(0);
        (await db.Set<IdentityUserRole<Guid>>().CountAsync(Cancellation)).Should().Be(0);
        (await db.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(0);
        (await db.Set<OutboxMessage>().CountAsync(Cancellation)).Should().Be(0);
    }

    private sealed class RejectSecondUserValidation : IUserValidator<User>
    {
        private int calls;
        public Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user) => Task.FromResult(
            ++calls == 2 ? IdentityResult.Failed(new IdentityError { Code = "RoleRejected", Description = "Injected role validation failure" }) : IdentityResult.Success);
    }
}
