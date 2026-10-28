using Cinema.Application.Account.Commands.SetMyDateOfBirth;
using Cinema.Application.Account.Commands.UpdateProfile;
using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public sealed partial class CustomerFactsPostgresTests
{
    private static async Task<Result> SetDobAsync(AsyncServiceScope scope, Guid id, DateTime date, bool profile)
    {
        ICurrentUserService current = Substitute.For<ICurrentUserService>(); current.UserId.Returns(id);
        if (profile)
            return await ActivatorUtilities.CreateInstance<UpdateProfileCommandHandler>(scope.ServiceProvider, current,
                NullLogger<UpdateProfileCommandHandler>.Instance).Handle(new UpdateProfileCommand("Test", "User", date), Cancellation);
        Result<SetMyDateOfBirthResponse> result = await ActivatorUtilities.CreateInstance<SetMyDateOfBirthCommandHandler>(
            scope.ServiceProvider, current, NullLogger<SetMyDateOfBirthCommandHandler>.Instance)
            .Handle(new SetMyDateOfBirthCommand(date), Cancellation);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Dob_Entry_Points_Commit_Source_And_Compatible_Messages_Without_Changing_Replay_Semantics(bool profile, bool registered)
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope seed = database.Services.CreateAsyncScope())
        {
            if (registered) await CreateSourceAsync(seed, id); else await CreateUserAndRoleAsync(seed, id);
        }
        DateTime date = new(1992, 2, 29);
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
            (await SetDobAsync(scope, id, date, profile)).IsSuccess.Should().BeTrue();
        Guid eventId;
        DateTime? firstSet;
        await using (AsyncServiceScope verify = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = AssertSharedContext(verify);
            User user = await db.Users.SingleAsync(Cancellation);
            user.CustomerFactsRevision.Should().Be(registered ? 2 : 1);
            user.RegisteredAtUtc.HasValue.Should().Be(registered);
            user.DobFirstSetAtUtc.Should().NotBeNull(); firstSet = user.DobFirstSetAtUtc;
            CustomerFactRevision fact = await db.CustomerFactRevisions.OrderByDescending(x => x.Revision).FirstAsync(Cancellation);
            eventId = fact.EventId;
            CustomerFactsEnvelope envelope = CustomerFactsEnvelope.FromJson(fact.Snapshot);
            envelope.Data.Kind.Should().Be("DateOfBirthSet");
            envelope.Data.DateOfBirth.Should().Be("1992-02-29");
            List<OutboxMessage> messages = await db.Set<OutboxMessage>().ToListAsync(Cancellation);
            messages.Count(x => x.MessageType.Contains(nameof(NestJsUserDateOfBirthSetEvent))).Should().Be(1);
            messages.Count(x => x.MessageType.Contains(nameof(UserProfileUpdatedMessage))).Should().Be(profile ? 1 : 0);
            var legacy = System.Text.Json.JsonDocument.Parse(messages.Single(x => x.MessageType.Contains(nameof(NestJsUserDateOfBirthSetEvent))).Body);
            legacy.RootElement.GetProperty("data").GetProperty("occurredAtUtc").GetDateTime().Should().Be(user.DobFirstSetAtUtc);
        }
        await using (AsyncServiceScope repeat = database.Services.CreateAsyncScope())
            (await SetDobAsync(repeat, id, date, profile)).IsSuccess.Should().Be(profile);
        await using (AsyncServiceScope omitted = database.Services.CreateAsyncScope())
        {
            ICurrentUserService current = Substitute.For<ICurrentUserService>(); current.UserId.Returns(id);
            (await ActivatorUtilities.CreateInstance<UpdateProfileCommandHandler>(omitted.ServiceProvider, current,
                NullLogger<UpdateProfileCommandHandler>.Instance).Handle(new UpdateProfileCommand("Name", "Only", null), Cancellation))
                .IsSuccess.Should().BeTrue();
        }
        await using AsyncServiceScope final = database.Services.CreateAsyncScope();
        ApplicationDbContext finalDb = AssertSharedContext(final);
        (await finalDb.Users.SingleAsync(Cancellation)).DobFirstSetAtUtc.Should().Be(firstSet);
        (await finalDb.CustomerFactRevisions.OrderByDescending(x => x.Revision).FirstAsync(Cancellation)).EventId.Should().Be(eventId);
        (await finalDb.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(registered ? 2 : 1);
        (await finalDb.Set<OutboxMessage>().CountAsync(x => x.MessageType.Contains(nameof(NestJsUserDateOfBirthSetEvent)), Cancellation)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_Dob_Handlers_Wait_On_The_Same_Row_Then_Only_One_Value_Commits()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope seed = database.Services.CreateAsyncScope()) await CreateUserAndRoleAsync(seed, id);
        await using AsyncServiceScope a = database.Services.CreateAsyncScope();
        await using AsyncServiceScope b = database.Services.CreateAsyncScope();
        // Deliberately stale tracked users must be reloaded after acquiring the lock.
        await AssertSharedContext(a).Users.SingleAsync(Cancellation);
        await AssertSharedContext(b).Users.SingleAsync(Cancellation);
        NpgsqlConnectionStringBuilder target = new(database.AdminConnection) { Database = database.Name };
        await using NpgsqlConnection barrier = new(target.ConnectionString);
        await barrier.OpenAsync(Cancellation);
        await using NpgsqlTransaction held = await barrier.BeginTransactionAsync(Cancellation);
        await using (NpgsqlCommand hold = new("SELECT id FROM \"AspNetUsers\" WHERE id=@id FOR UPDATE", barrier, held))
        { hold.Parameters.AddWithValue("id", id); await hold.ExecuteNonQueryAsync(Cancellation); }
        Task<Result> first = SetDobAsync(a, id, new DateTime(1990, 1, 1), false);
        Task<Result> second = SetDobAsync(b, id, new DateTime(1992, 2, 29), true);
        try { await WaitForUserLockWaitersAsync(database, 2); }
        finally { await held.RollbackAsync(Cancellation); }
        Result[] results = await Task.WhenAll(first, second);
        results.Count(x => x.IsSuccess).Should().Be(1);
        results.Single(x => x.IsFailure).Error.Code.Should().Be("User.DateOfBirthAlreadySet");
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        (await db.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(1);
        (await db.Users.SingleAsync(Cancellation)).CustomerFactsRevision.Should().Be(1);
        (await db.Set<OutboxMessage>().CountAsync(x => x.MessageType.Contains(nameof(NestJsUserDateOfBirthSetEvent)), Cancellation)).Should().Be(1);
    }

    private static async Task WaitForUserLockWaitersAsync(ProofDatabase database, int expected)
    {
        await using NpgsqlConnection observer = new(database.AdminConnection);
        await observer.OpenAsync(Cancellation);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using NpgsqlCommand query = new("SELECT count(*) FROM pg_stat_activity WHERE datname=@db AND wait_event_type='Lock' AND (query ILIKE '%AspNetUsers%' OR query ILIKE '%AspNetUserRoles%')", observer);
            query.Parameters.AddWithValue("db", database.Name);
            if (Convert.ToInt32(await query.ExecuteScalarAsync(deadline.Token)) >= expected) return;
            await Task.Delay(20, deadline.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_Password_Or_Role_Update_Cannot_Overwrite_First_Dob_Facts(bool role)
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope seed = database.Services.CreateAsyncScope())
        {
            await CreateUserAndRoleAsync(seed, id);
            (await seed.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>()
                .CreateAsync(new IdentityRole<Guid>("Admin"))).Succeeded.Should().BeTrue();
        }
        await using AsyncServiceScope dobScope = database.Services.CreateAsyncScope();
        await using AsyncServiceScope identityScope = database.Services.CreateAsyncScope();
        UserManager<User> manager = identityScope.ServiceProvider.GetRequiredService<UserManager<User>>();
        User stale = await manager.FindByIdAsync(id.ToString()) ?? throw new InvalidOperationException();
        NpgsqlConnectionStringBuilder target = new(database.AdminConnection) { Database = database.Name };
        await using NpgsqlConnection barrier = new(target.ConnectionString);
        await barrier.OpenAsync(Cancellation);
        await using NpgsqlTransaction held = await barrier.BeginTransactionAsync(Cancellation);
        await using (NpgsqlCommand hold = new("SELECT id FROM \"AspNetUsers\" WHERE id=@id FOR UPDATE", barrier, held))
        { hold.Parameters.AddWithValue("id", id); await hold.ExecuteNonQueryAsync(Cancellation); }
        Task<Result> dob = SetDobAsync(dobScope, id, new DateTime(1992, 2, 29), false);
        Task<IdentityResult> identity = role ? manager.AddToRoleAsync(stale, "Admin")
            : manager.ChangePasswordAsync(stale, "Proof1!password", "Changed2!password");
        try { await WaitForUserLockWaitersAsync(database, 2); }
        finally { await held.RollbackAsync(Cancellation); }
        (await dob).IsSuccess.Should().BeTrue();
        IdentityResult identityResult = await identity;
        if (!identityResult.Succeeded)
            identityResult.Errors.Should().Contain(error => error.Code == "ConcurrencyFailure");
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        User user = await db.Users.SingleAsync(Cancellation);
        user.DateOfBirth.Should().Be(new DateTime(1992, 2, 29, 0, 0, 0, DateTimeKind.Utc));
        user.DobFirstSetAtUtc.Should().NotBeNull(); user.CustomerFactsRevision.Should().Be(1);
        (await db.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(1);
        UserManager<User> fresh = verify.ServiceProvider.GetRequiredService<UserManager<User>>();
        if (role) (await fresh.IsInRoleAsync(user, "Admin")).Should().Be(identityResult.Succeeded);
        else (await fresh.CheckPasswordAsync(user, "Changed2!password")).Should().Be(identityResult.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dob_Revision_Exhaustion_Rejects_Without_Partial_Mutation(bool profile)
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope seed = database.Services.CreateAsyncScope())
        {
            await CreateUserAndRoleAsync(seed, id);
            await AssertSharedContext(seed).Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AspNetUsers\" SET customer_facts_revision=2147483647 WHERE id={id}", Cancellation);
        }
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
            (await SetDobAsync(scope, id, new DateTime(1990, 1, 1), profile)).IsFailure.Should().BeTrue();
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        (await db.Users.SingleAsync(Cancellation)).DateOfBirth.Should().BeNull();
        (await db.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(0);
        (await db.Set<OutboxMessage>().CountAsync(Cancellation)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dob_Save_Failure_After_Legacy_Staging_Rolls_Back_Source_History_And_All_Messages(bool profile)
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope seed = database.Services.CreateAsyncScope())
        {
            await CreateUserAndRoleAsync(seed, id);
            await AssertSharedContext(seed).Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_customer_fact() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.body LIKE '%CUSTOMER_FACTS_CHANGED%' THEN RAISE EXCEPTION 'injected after legacy staging'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_customer_fact BEFORE INSERT ON outbox_message FOR EACH ROW EXECUTE FUNCTION fail_customer_fact()", Cancellation);
        }
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            Func<Task> set = () => SetDobAsync(scope, id, new DateTime(1990, 1, 1), profile);
            await set.Should().ThrowAsync<DbUpdateException>();
        }
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        User user = await db.Users.SingleAsync(Cancellation);
        user.DateOfBirth.Should().BeNull(); user.DobFirstSetAtUtc.Should().BeNull(); user.CustomerFactsRevision.Should().Be(0);
        (await db.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(0);
        (await db.Set<OutboxMessage>().CountAsync(Cancellation)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dob_Lost_Commit_Acknowledgement_Then_Repeated_Command_Has_One_Effect(bool profile)
    {
        LostCommitAcknowledgement fault = new();
        await using ProofDatabase database = await CreateDatabaseAsync(fault);
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope seed = database.Services.CreateAsyncScope()) await CreateUserAndRoleAsync(seed, id);
        fault.Armed = true;
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            Func<Task> set = () => SetDobAsync(scope, id, new DateTime(1990, 1, 1), profile);
            await set.Should().ThrowAsync<NpgsqlException>();
        }
        await using (AsyncServiceScope repeat = database.Services.CreateAsyncScope())
            (await SetDobAsync(repeat, id, new DateTime(1990, 1, 1), profile)).IsSuccess.Should().Be(profile);
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        (await db.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(1);
        (await db.Set<OutboxMessage>().CountAsync(x => x.MessageType.Contains(nameof(NestJsUserDateOfBirthSetEvent)), Cancellation)).Should().Be(1);
    }
}
