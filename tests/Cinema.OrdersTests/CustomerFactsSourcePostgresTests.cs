using System.Data;
using Cinema.Application.Common.Contracts;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Services;
using FluentAssertions;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Cinema.OrdersTests;

public sealed partial class CustomerFactsPostgresTests
{
    private static async Task CreateSourceAsync(AsyncServiceScope scope, Guid userId)
    {
        CustomerFactsWriter writer = scope.ServiceProvider.GetRequiredService<CustomerFactsWriter>();
        await writer.ExecuteAsync(userId, async ct =>
        {
            User user = NewUser(userId);
            user.RecordRegistration(new DateTime(2024, 2, 29, 22, 30, 0, DateTimeKind.Utc));
            await writer.StageAsync(user, Guid.NewGuid(), "Registered", ct);
            UserManager<User> users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            (await users.CreateAsync(user, "Proof1!password")).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(user, "User")).Succeeded.Should().BeTrue();
            return Result.Success();
        }, Cancellation);
    }

    [Fact]
    public async Task Source_History_And_Outbox_Commit_And_Reconstruct_In_A_New_Scope()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope()) await CreateSourceAsync(scope, id);
        string original;
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            User user = await context.Users.SingleAsync(Cancellation);
            CustomerFactRevision history = await context.CustomerFactRevisions.SingleAsync(Cancellation);
            user.CustomerFactsRevision.Should().Be(1);
            user.RegisteredAtUtc.Should().Be(new DateTime(2024, 2, 29, 22, 30, 0, DateTimeKind.Utc));
            user.DobFirstSetAtUtc.Should().BeNull();
            original = history.Snapshot;
            CustomerFactsEnvelope.FromUser(user, history.EventId, "Registered").ToJson().Should().Be(original);
            // MassTransit indents raw JSON; compare all normalized v1 facts, not whitespace.
            CustomerFactsEnvelope.FromJson((await context.Set<OutboxMessage>().SingleAsync(Cancellation)).Body)
                .ToJson().Should().Be(original);
        }
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            CustomerFactsWriter writer = scope.ServiceProvider.GetRequiredService<CustomerFactsWriter>();
            await writer.ExecuteAsync(id, async ct =>
            {
                User user = await context.Users.SingleAsync(ct);
                user.RecordFirstDateOfBirth(new DateOnly(1992, 2, 29), new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc));
                await writer.StageAsync(user, Guid.NewGuid(), "DateOfBirthSet", ct);
                (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().UpdateAsync(user)).Succeeded.Should().BeTrue();
                return Result.Success();
            }, Cancellation);
        }
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        (await db.CustomerFactRevisions.SingleAsync(x => x.Revision == 1, Cancellation)).Snapshot.Should().Be(original);
        CustomerFactRevision dob = await db.CustomerFactRevisions.SingleAsync(x => x.Revision == 2, Cancellation);
        CustomerFactsEnvelope.FromJson(dob.Snapshot).Data.DateOfBirth.Should().Be("1992-02-29");
        (await db.Users.SingleAsync(Cancellation)).CustomerFactsRevision.Should().Be(2);
        (await db.Set<OutboxMessage>().CountAsync(Cancellation)).Should().Be(2);
    }

    [Fact]
    public async Task Source_Event_Id_Is_Never_Replaced_By_Ef_Key_Generation()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            CustomerFactsWriter writer = scope.ServiceProvider.GetRequiredService<CustomerFactsWriter>();
            Result result = await writer.ExecuteAsync(id, async ct =>
            {
                User user = NewUser(id);
                user.RecordRegistration(DateTime.UtcNow);
                // The unchanged v1 UUID schema accepts this value; the store must not replace it.
                await writer.StageAsync(user, Guid.Empty, "Registered", ct);
                (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, "Proof1!password"))
                    .Succeeded.Should().BeTrue();
                return Result.Success();
            }, Cancellation);
            result.IsSuccess.Should().BeTrue();
        }
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        CustomerFactRevision fact = await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CustomerFactRevisions.SingleAsync(Cancellation);
        fact.EventId.Should().Be(Guid.Empty);
        CustomerFactsEnvelope.FromJson(fact.Snapshot).Data.EventId.Should().Be(Guid.Empty);
    }
    [Fact]
    public async Task Source_Rollback_Is_Atomic_And_Failed_Scope_Cannot_Be_Reused()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        int attempts = 0;
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            CustomerFactsWriter writer = scope.ServiceProvider.GetRequiredService<CustomerFactsWriter>();
            Func<Task> operation = () => writer.ExecuteAsync(id, async ct =>
            {
                attempts++;
                User user = NewUser(id);
                user.RecordRegistration(DateTime.UtcNow);
                await writer.StageAsync(user, Guid.NewGuid(), "Registered", ct);
                (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, "Proof1!password"))
                    .Succeeded.Should().BeTrue();
                await context.SaveChangesAsync(ct);
                (await context.CustomerFactRevisions.CountAsync(ct)).Should().Be(1);
                throw new NpgsqlException("Injected failure after source save", new TimeoutException());
            }, Cancellation);
            await operation.Should().ThrowAsync<NpgsqlException>();
            attempts.Should().Be(1);
            context.ChangeTracker.Entries().Should().BeEmpty();
            Func<Task> reuse = () => context.SaveChangesAsync(Cancellation);
            await reuse.Should().ThrowAsync<InvalidOperationException>();
        }
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        (await db.Users.CountAsync(Cancellation)).Should().Be(0);
        (await db.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(0);
        (await db.Set<OutboxMessage>().CountAsync(Cancellation)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Immutable_History_Rejects_Ordinary_Update_And_Delete(bool delete)
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope()) await CreateSourceAsync(scope, id);
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            CustomerFactRevision history = await context.CustomerFactRevisions.SingleAsync(Cancellation);
            if (delete) context.CustomerFactRevisions.Remove(history);
            else context.Entry(history).Property(x => x.Snapshot).CurrentValue = "{}";
            Func<Task> save = () => context.SaveChangesAsync(Cancellation);
            await save.Should().ThrowAsync<InvalidOperationException>();
        }
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        (await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>().CustomerFactRevisions.CountAsync(Cancellation))
            .Should().Be(1);
    }

    [Fact]
    public async Task Database_Rejects_Duplicate_Revision_And_Invalid_Identity_Checks()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope()) await CreateSourceAsync(scope, id);
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext db = AssertSharedContext(verify);
        User user = await db.Users.SingleAsync(Cancellation);
        Guid duplicateId = Guid.NewGuid();
        string snapshot = CustomerFactsEnvelope.FromUser(user, duplicateId, "Registered").ToJson();
        Func<Task> duplicate = () => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO customer_fact_revisions(event_id,user_id,revision,snapshot) VALUES ({duplicateId},{id},1,{snapshot})", Cancellation);
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        Func<Task> invalid = () => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO customer_fact_revisions(event_id,user_id,revision,snapshot) VALUES ({Guid.NewGuid()},{id},0,{snapshot})", Cancellation);
        (await invalid.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task Ordinary_Identity_Update_Preserves_Source_And_Rejects_Stale_Head()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope seed = database.Services.CreateAsyncScope())
            await CreateUserAndRoleAsync(seed, id); // Deliberately legacy unknown account.
        await using AsyncServiceScope staleScope = database.Services.CreateAsyncScope();
        UserManager<User> staleManager = staleScope.ServiceProvider.GetRequiredService<UserManager<User>>();
        User stale = await staleManager.FindByIdAsync(id.ToString()) ?? throw new InvalidOperationException();
        await using (AsyncServiceScope current = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = AssertSharedContext(current);
            CustomerFactsWriter writer = current.ServiceProvider.GetRequiredService<CustomerFactsWriter>();
            await writer.ExecuteAsync(id, async ct =>
            {
                User user = await db.Users.SingleAsync(ct);
                user.RecordFirstDateOfBirth(new DateOnly(1992, 2, 29), DateTime.UtcNow);
                await writer.StageAsync(user, Guid.NewGuid(), "DateOfBirthSet", ct);
                (await current.ServiceProvider.GetRequiredService<UserManager<User>>().UpdateAsync(user)).Succeeded.Should().BeTrue();
                return Result.Success();
            }, Cancellation);
        }
        stale.FirstName = "Stale";
        (await staleManager.UpdateAsync(stale)).Succeeded.Should().BeFalse();
        await using AsyncServiceScope fresh = database.Services.CreateAsyncScope();
        ApplicationDbContext context = AssertSharedContext(fresh);
        UserManager<User> manager = fresh.ServiceProvider.GetRequiredService<UserManager<User>>();
        User updated = await context.Users.SingleAsync(Cancellation);
        DateTime? first = updated.DobFirstSetAtUtc;
        updated.FirstName = "Fresh";
        (await manager.UpdateAsync(updated)).Succeeded.Should().BeTrue();
        updated.RegisteredAtUtc.Should().BeNull();
        updated.DobFirstSetAtUtc.Should().Be(first);
        context.Entry(updated).Property(x => x.DobFirstSetAtUtc).CurrentValue = DateTime.UtcNow.AddDays(1);
        Func<Task> overwrite = () => context.SaveChangesAsync(Cancellation);
        await overwrite.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Writer_Rejects_Outer_Retry_And_Does_Not_Replay_Ambiguous_Commit()
    {
        LostCommitAcknowledgement fault = new();
        await using ProofDatabase database = await CreateDatabaseAsync(fault);
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = AssertSharedContext(scope);
            CustomerFactsWriter writer = scope.ServiceProvider.GetRequiredService<CustomerFactsWriter>();
            int attempts = 0;
            Func<Task> nested = () => db.Database.CreateExecutionStrategy().ExecuteAsync(() =>
                writer.ExecuteAsync(id, _ => { attempts++; return Task.FromResult(Result.Success()); }, Cancellation));
            await nested.Should().ThrowAsync<InvalidOperationException>();
            attempts.Should().Be(0);
            Func<Task> commit = () => writer.ExecuteAsync(id, async ct =>
            {
                attempts++;
                User user = NewUser(id);
                user.RecordRegistration(DateTime.UtcNow);
                await writer.StageAsync(user, Guid.NewGuid(), "Registered", ct);
                (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, "Proof1!password"))
                    .Succeeded.Should().BeTrue();
                fault.Armed = true;
                return Result.Success();
            }, Cancellation);
            await commit.Should().ThrowAsync<NpgsqlException>();
            attempts.Should().Be(1);
            Func<Task> reuse = () => db.SaveChangesAsync(Cancellation);
            await reuse.Should().ThrowAsync<InvalidOperationException>();
        }
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext context = AssertSharedContext(verify);
        (await context.Users.CountAsync(Cancellation)).Should().Be(1);
        (await context.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(1);
        (await context.Set<OutboxMessage>().CountAsync(Cancellation)).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Known_Source_Instants_Reject_Overwrite_In_Both_Save_Overloads(bool firstDob)
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            await CreateSourceAsync(scope, id);
            if (firstDob)
            {
                CustomerFactsWriter writer = scope.ServiceProvider.GetRequiredService<CustomerFactsWriter>();
                ApplicationDbContext context = AssertSharedContext(scope);
                await writer.ExecuteAsync(id, async ct =>
                {
                    User user = await context.Users.SingleAsync(ct);
                    user.RecordFirstDateOfBirth(new DateOnly(1992, 2, 29), new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc));
                    await writer.StageAsync(user, Guid.NewGuid(), "DateOfBirthSet", ct);
                    return Result.Success();
                }, Cancellation);
            }
        }
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            User user = await context.Users.SingleAsync(Cancellation);
            if (firstDob)
            {
                context.Entry(user).Property(x => x.DobFirstSetAtUtc).CurrentValue = DateTime.UtcNow;
                Func<Task> save = () => context.SaveChangesAsync(Cancellation);
                await save.Should().ThrowAsync<InvalidOperationException>();
            }
            else
            {
                context.Entry(user).Property(x => x.RegisteredAtUtc).CurrentValue = DateTime.UtcNow;
                Action save = () => context.SaveChanges();
                save.Should().Throw<InvalidOperationException>();
            }
        }
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        User persisted = await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(Cancellation);
        persisted.RegisteredAtUtc.Should().Be(new DateTime(2024, 2, 29, 22, 30, 0, DateTimeKind.Utc));
        if (firstDob) persisted.DobFirstSetAtUtc.Should().Be(new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc));
    }
    [Fact]
    public async Task Rejected_Result_Rolls_Back_Intermediate_Source_Identity_And_Outbox_Saves()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = AssertSharedContext(scope);
            CustomerFactsWriter writer = scope.ServiceProvider.GetRequiredService<CustomerFactsWriter>();
            Result result = await writer.ExecuteAsync(id, async ct =>
            {
                User user = NewUser(id);
                user.RecordRegistration(DateTime.UtcNow);
                await writer.StageAsync(user, Guid.NewGuid(), "Registered", ct);
                (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, "Proof1!password"))
                    .Succeeded.Should().BeTrue();
                (await db.CustomerFactRevisions.CountAsync(ct)).Should().Be(1);
                return Result.Failure(new Error("Proof.Rejected", "Injected checked Identity failure"));
            }, Cancellation);
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("Proof.Rejected");
            Func<Task> reuse = () => db.SaveChangesAsync(Cancellation);
            await reuse.Should().ThrowAsync<InvalidOperationException>();
        }
        await using AsyncServiceScope verify = database.Services.CreateAsyncScope();
        ApplicationDbContext context = AssertSharedContext(verify);
        (await context.Users.CountAsync(Cancellation)).Should().Be(0);
        (await context.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(0);
        (await context.Set<OutboxMessage>().CountAsync(Cancellation)).Should().Be(0);
    }
    [Fact]
    public async Task Source_Head_Cannot_Skip_History_And_Revision_Exhaustion_Does_Not_Mutate_Dob()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope seed = database.Services.CreateAsyncScope()) await CreateUserAndRoleAsync(seed, id);
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            User user = await context.Users.SingleAsync(Cancellation);
            user.RecordFirstDateOfBirth(new DateOnly(1990, 1, 1), DateTime.UtcNow);
            Func<Task> missingHistory = () => context.SaveChangesAsync(Cancellation);
            await missingHistory.Should().ThrowAsync<InvalidOperationException>();
        }
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            // Deliberate boundary fixture via SQL, never a production source mutation.
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AspNetUsers\" SET customer_facts_revision = 2147483647 WHERE id = {id}", Cancellation);
            User user = await context.Users.SingleAsync(Cancellation);
            Action overflow = () => user.RecordFirstDateOfBirth(new DateOnly(1990, 1, 1), DateTime.UtcNow);
            overflow.Should().Throw<OverflowException>();
            user.DateOfBirth.Should().BeNull();
            user.DobFirstSetAtUtc.Should().BeNull();
            (await context.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(0);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_Applies_To_Clean_And_Prior_Schema_Without_Inventing_Legacy_Facts(bool prior)
    {
        string connection = Environment.GetEnvironmentVariable("CINEMA_TEST_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException("Disposable test connection required");
        NpgsqlConnectionStringBuilder builder = new(connection);
        if (builder.Database?.StartsWith("cinema_orders_test_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Not a disposable test target");
        string name = $"cinema_orders_test_migration_{Guid.NewGuid():N}";
        builder.Database = "postgres";
        await using NpgsqlConnection admin = new(builder.ConnectionString);
        await admin.OpenAsync(Cancellation);
        await using (NpgsqlCommand create = new($"CREATE DATABASE {name}", admin)) await create.ExecuteNonQueryAsync(Cancellation);
        builder.Database = name;
        try
        {
            await using NpgsqlDataSource source = DatabaseContextConfiguration.CreateDataSource(builder.ConnectionString);
            DbContextOptionsBuilder<ApplicationDbContext> options = new();
            DatabaseContextConfiguration.Configure(options, source);
            await using ApplicationDbContext context = new(options.Options);
            Guid legacyId = Guid.NewGuid();
            if (prior)
            {
                await context.GetService<IMigrator>().MigrateAsync("20261023192457_PersistOrderPaidAt", Cancellation);
                await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AspNetUsers\" (id,user_name,date_of_birth,email_confirmed,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count) VALUES ({legacyId},'legacy',{new DateTime(1992,2,29,0,0,0,DateTimeKind.Utc)},false,false,false,false,0)", Cancellation);
            }
            await context.Database.MigrateAsync(Cancellation);
            (await context.Database.GetPendingMigrationsAsync(Cancellation)).Should().BeEmpty();
            if (prior)
            {
                User user = await context.Users.SingleAsync(x => x.Id == legacyId, Cancellation);
                user.DateOfBirth.Should().Be(new DateTime(1992, 2, 29, 0, 0, 0, DateTimeKind.Utc));
                user.RegisteredAtUtc.Should().BeNull();
                user.DobFirstSetAtUtc.Should().BeNull();
                user.CustomerFactsRevision.Should().Be(0);
            }
            (await context.CustomerFactRevisions.CountAsync(Cancellation)).Should().Be(0);
        }
        finally
        {
            await using NpgsqlCommand drop = new($"DROP DATABASE {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(Cancellation);
        }
    }
}
