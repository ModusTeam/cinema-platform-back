using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Cinema.OrdersTests;

// T002 characterizes existing wiring only: no customer-facts source schema/producer.
public sealed partial class CustomerFactsPostgresTests(PostgresSchemaTemplate template) : IClassFixture<PostgresSchemaTemplate>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bare_Explicit_Transaction_Is_Rejected_By_Production_Retry_Strategy()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue();
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(Cancellation);
        Func<Task> operation = async () =>
        {
            await scope.ServiceProvider.GetRequiredService<UserManager<User>>()
                .CreateAsync(NewUser(Guid.NewGuid()), "Proof1!password");
        };
        await operation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*NpgsqlRetryingExecutionStrategy*does not support user-initiated transactions*");
    }

    [Fact]
    public async Task User_Role_And_Outbox_Commit_Together_Without_Available_Broker()
    {
        await using ProofDatabase database = await CreateDatabaseAsync(useProductionDataSource: true);
        // Reserved but non-listening loopback port: there is no broker to accept a Send.
        using TcpClient probe = new();
        Func<Task> connect = () => probe.ConnectAsync(IPAddress.Loopback, database.BrokerPort, Cancellation).AsTask();
        await connect.Should().ThrowAsync<SocketException>();
        Guid userId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            Guid transactionId = Guid.Empty;
            await ExecuteProofAsync(context, async () =>
            {
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(Cancellation);
                transactionId = transaction.TransactionId;
                await CreateUserAndRoleAsync(scope, userId);
                await StageAsync(scope, userId, messageId);
                await context.SaveChangesAsync(Cancellation);
                // A separate connection cannot see any of the uncommitted operation.
                await AssertStateAsync(database, userId, messageId, present: false);
                await transaction.CommitAsync(Cancellation);
            });
            database.Saves.Observed.Should().HaveCountGreaterThanOrEqualTo(3);
            database.Saves.Observed.Should().OnlyContain(save =>
                ReferenceEquals(save.Context, context) && save.TransactionId == transactionId);
            context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue();
        }
        await AssertStateAsync(database, userId, messageId, present: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_After_Identity_Save_Or_Outbox_Save_Rolls_Back_All_Writes(bool afterOutboxSave)
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid userId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            Func<Task> operation = () => ExecuteProofAsync(context, async () =>
            {
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(Cancellation);
                await CreateUserAndRoleAsync(scope, userId);
                (await context.Users.AnyAsync(user => user.Id == userId, Cancellation)).Should().BeTrue();
                (await context.Set<IdentityUserRole<Guid>>().AnyAsync(role => role.UserId == userId, Cancellation))
                    .Should().BeTrue();
                if (afterOutboxSave)
                {
                    await StageAsync(scope, userId, messageId);
                    await context.SaveChangesAsync(Cancellation);
                    (await context.Set<OutboxMessage>().AnyAsync(message => message.MessageId == messageId, Cancellation))
                        .Should().BeTrue();
                }
                throw new IOException("Injected failure before commit");
            });
            await operation.Should().ThrowAsync<IOException>();
        }
        // The failed transaction and its scoped outbox are disposed before verification.
        await AssertStateAsync(database, userId, messageId, present: false);
        await using AsyncServiceScope unrelated = database.Services.CreateAsyncScope();
        await unrelated.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(Cancellation);
        await AssertStateAsync(database, userId, messageId, present: false);
    }

    [Fact]
    public async Task Transient_Failure_Does_Not_Replay_Multi_Save_Callback()
    {
        await using ProofDatabase database = await CreateDatabaseAsync();
        Guid userId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        int attempts = 0;
        NpgsqlException transient = new("Injected transient failure", new TimeoutException());
        transient.IsTransient.Should().BeTrue();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            Func<Task> operation = () => ExecuteProofAsync(context, async () =>
            {
                attempts++;
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(Cancellation);
                await CreateUserAndRoleAsync(scope, userId);
                await StageAsync(scope, userId, messageId);
                await context.SaveChangesAsync(Cancellation);
                throw transient;
            });
            await operation.Should().ThrowAsync<NpgsqlException>();
            attempts.Should().Be(1);
            context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue();
        }
        await AssertStateAsync(database, userId, messageId, present: false);
    }

    [Fact]
    public async Task Lost_Commit_Acknowledgement_Leaves_Committed_Writes_Without_Callback_Replay()
    {
        LostCommitAcknowledgement fault = new();
        await using ProofDatabase database = await CreateDatabaseAsync(fault);
        Guid userId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        int attempts = 0;
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            ApplicationDbContext context = AssertSharedContext(scope);
            Func<Task> operation = () => ExecuteProofAsync(context, async () =>
            {
                attempts++;
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(Cancellation);
                await CreateUserAndRoleAsync(scope, userId);
                await StageAsync(scope, userId, messageId);
                await context.SaveChangesAsync(Cancellation);
                fault.Armed = true;
                await transaction.CommitAsync(Cancellation);
            });
            await operation.Should().ThrowAsync<NpgsqlException>();
            attempts.Should().Be(1);
            fault.Failures.Should().Be(1);
        }
        // Fault is thrown only after the actual provider CommitAsync completed.
        // The caller saw a failure, but a new connection proves commit, not rollback.
        await AssertStateAsync(database, userId, messageId, present: true);
    }

    private static User NewUser(Guid id) => new()
    {
        Id = id,
        UserName = $"proof-{id:N}@example.invalid",
        Email = $"proof-{id:N}@example.invalid"
    };

    private static ApplicationDbContext AssertSharedContext(AsyncServiceScope scope)
    {
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        scope.ServiceProvider.GetRequiredService<IApplicationDbContext>().Should().BeSameAs(context);
        scope.ServiceProvider.GetRequiredService<IUserStore<User>>()
            .Should().BeAssignableTo<UserStore<User, IdentityRole<Guid>, ApplicationDbContext, Guid,
                IdentityUserClaim<Guid>, IdentityUserRole<Guid>, IdentityUserLogin<Guid>,
                IdentityUserToken<Guid>, IdentityRoleClaim<Guid>>>()
            .Which.Context.Should().BeSameAs(context);
        scope.ServiceProvider.GetRequiredService<IRoleStore<IdentityRole<Guid>>>()
            .Should().BeAssignableTo<RoleStore<IdentityRole<Guid>, ApplicationDbContext, Guid,
                IdentityUserRole<Guid>, IdentityRoleClaim<Guid>>>()
            .Which.Context.Should().BeSameAs(context);
        scope.ServiceProvider.GetRequiredService<ISendEndpointProvider>()
            .Should().BeSameAs(scope.ServiceProvider.GetRequiredService<ISendEndpointProvider>());
        context.Database.CreateExecutionStrategy().Should().BeAssignableTo<ExecutionStrategy>()
            .Which.RetriesOnFailure.Should().BeTrue();
        return context;
    }

    private static async Task CreateUserAndRoleAsync(AsyncServiceScope scope, Guid userId)
    {
        UserManager<User> users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        User user = NewUser(userId);
        (await users.CreateAsync(user, "Proof1!password")).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, "User"))
            .Succeeded.Should().BeTrue();
    }

    private static async Task StageAsync(AsyncServiceScope scope, Guid userId, Guid messageId)
    {
        ISendEndpoint endpoint = await scope.ServiceProvider.GetRequiredService<ISendEndpointProvider>()
            .GetSendEndpoint(new Uri("queue:loyalty_ticket_purchased"));
        await endpoint.Send(new NestJsUserDateOfBirthSetEvent(LoyaltyEventPatterns.UserDateOfBirthSet,
                new UserDateOfBirthSetPayload(userId, "1990-01-01", new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc))),
            context => context.MessageId = messageId, Cancellation).WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
    }

    // Zero-retry wrapper keeps nested Identity saves inside one supported execution scope.
    private static Task ExecuteProofAsync(ApplicationDbContext context, Func<Task> operation)
        => new ProofExecutionStrategy(context).ExecuteAsync(operation);

    private static async Task AssertStateAsync(ProofDatabase database, Guid userId, Guid messageId, bool present)
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await context.Users.CountAsync(user => user.Id == userId, Cancellation)).Should().Be(present ? 1 : 0);
        (await context.Set<IdentityUserRole<Guid>>().CountAsync(role => role.UserId == userId, Cancellation))
            .Should().Be(present ? 1 : 0);
        List<OutboxMessage> messages = await context.Set<OutboxMessage>()
            .Where(message => message.MessageId == messageId).ToListAsync(Cancellation);
        messages.Should().HaveCount(present ? 1 : 0);
        if (present)
        {
            IdentityRole<Guid> role = await context.Roles.SingleAsync(Cancellation);
            IdentityUserRole<Guid> assignment = await context.Set<IdentityUserRole<Guid>>()
                .SingleAsync(value => value.UserId == userId, Cancellation);
            assignment.RoleId.Should().Be(role.Id);
            (await context.Set<OutboxState>().CountAsync(Cancellation)).Should().Be(1);
            using JsonDocument body = JsonDocument.Parse(messages[0].Body);
            body.RootElement.GetProperty("pattern").GetString().Should().Be("USER_DATE_OF_BIRTH_SET");
            body.RootElement.GetProperty("data").GetProperty("userId").GetGuid().Should().Be(userId);
            body.RootElement.GetProperty("data").GetProperty("dateOfBirth").GetString().Should().Be("1990-01-01");
        }
        else
            (await context.Set<OutboxState>().CountAsync(Cancellation)).Should().Be(0);
    }

    private async Task<ProofDatabase> CreateDatabaseAsync(DbTransactionInterceptor? fault = null, bool useProductionDataSource = false,
        Action<IServiceCollection>? configureServices = null)
    {
        (string connection, string admin, string name) = await template.CreateDatabaseAsync();
        Socket unavailableBroker = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailableBroker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = unavailableBroker.LocalEndPoint.Should().BeOfType<IPEndPoint>().Which.Port;
        ServiceCollection services = new();
        services.AddLogging();
        services.AddScoped<Cinema.Infrastructure.Services.CustomerFactsWriter>();
        services.AddScoped<Cinema.Infrastructure.Services.CustomerRegistrationService>();
        if (useProductionDataSource) services.AddSingleton(DatabaseContextConfiguration.CreateDataSource(connection));
        SaveObservation saves = new();
        services.AddDbContext<ApplicationDbContext>((provider, options) =>
        {
            if (useProductionDataSource)
                DatabaseContextConfiguration.Configure(options, provider.GetRequiredService<NpgsqlDataSource>());
            else
                // Per-database tests share EF internal services instead of caching a
                // distinct service provider for each short-lived NpgsqlDataSource.
                options.UseNpgsql(connection, postgres =>
                {
                    postgres.UseVector();
                    postgres.EnableRetryOnFailure(3);
                }).UseSnakeCaseNamingConvention();
            options.AddInterceptors(saves);
            if (fault is not null) options.AddInterceptors(fault);
        });
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());
        services.AddIdentityCore<User>(options => options.User.RequireUniqueEmail = true)
            .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddMassTransit(registration =>
        {
            registration.AddEntityFrameworkOutbox<ApplicationDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
            });
            registration.UsingRabbitMq((_, bus) =>
            {
                bus.Host("127.0.0.1", (ushort)port, "/", host =>
                {
                    host.Username("unavailable");
                    host.Password("disposable-only");
                });
                bus.UseRawJsonSerializer();
            });
        });
        configureServices?.Invoke(services);
        ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        ProofDatabase database = new(provider, unavailableBroker, port, admin, name, saves);
        try
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>()
                .CreateAsync(new IdentityRole<Guid>("User"))).Succeeded.Should().BeTrue();
            saves.Observed.Clear();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    // Test-only equivalent of CheckoutExecutionStrategy; no production behavior is changed.
    private sealed class ProofExecutionStrategy(ApplicationDbContext context)
        : ExecutionStrategy(context.Database.GetService<ExecutionStrategyDependencies>(), 0, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }
    private sealed class SaveObservation : SaveChangesInterceptor
    {
        public List<(DbContext? Context, Guid? TransactionId)> Observed { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Observed.Add((eventData.Context, eventData.Context?.Database.CurrentTransaction?.TransactionId));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class LostCommitAcknowledgement : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public int Failures { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                Failures++;
                throw new NpgsqlException("Injected lost commit acknowledgement", new TimeoutException());
            }
            return Task.CompletedTask;
        }
    }

    private sealed record ProofDatabase(ServiceProvider Services, Socket BrokerSocket, int BrokerPort,
        string AdminConnection, string Name, SaveObservation Saves) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            BrokerSocket.Dispose();
            await using NpgsqlConnection admin = new(AdminConnection);
            await admin.OpenAsync();
            await using NpgsqlCommand drop = new($"DROP DATABASE {Name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
