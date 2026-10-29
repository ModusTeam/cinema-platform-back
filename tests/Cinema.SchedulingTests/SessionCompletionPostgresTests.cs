using Cinema.Application.Services;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Services;
using Cinema.OrdersTests;
using FluentAssertions;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using NSubstitute;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Text.Json;
namespace Cinema.SchedulingTests;
public class SessionCompletionPostgresTests(PostgresSchemaTemplate template) : IClassFixture<PostgresSchemaTemplate>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_commits_source_and_outbox_even_without_broker(bool inactive)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        Guid id = await Due(db, inactive);
        await using ServiceProvider services = Services(db);
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id, Ct)).Should().BeTrue();
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id, Ct)).Should().BeFalse();
        await using ApplicationDbContext verify = db.NewContext();
        Session session = await verify.Sessions.IgnoreQueryFilters().SingleAsync(Ct);
        SessionCompletionFact fact = await verify.Set<SessionCompletionFact>().SingleAsync(Ct);
        session.Status.Should().Be(SessionStatus.Ended);
        fact.CompletedAtUtc.Should().Be(session.EndTime);
        OutboxMessage message = await verify.Set<OutboxMessage>().SingleAsync(Ct);
        message.MessageId.Should().Be(fact.EventId);
        using var json = System.Text.Json.JsonDocument.Parse(message.Body);
        json.RootElement.GetProperty("data").GetProperty("completedAtUtc").GetString()
            .Should().EndWith(".123456Z");
        (await verify.Set<OutboxState>().CountAsync(Ct)).Should().Be(1);
        fact.Snapshot.Should().Contain(fact.EventId.ToString());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_after_staging_rolls_back_everything(bool afterSave)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        Guid id = await Due(db);
        await using ServiceProvider services = Services(db, new FailSave(afterSave));
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            Func<Task> complete = async () => await scope.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id, Ct);
            await complete.Should().ThrowAsync<IOException>();
        }
        await using ApplicationDbContext verify = db.NewContext();
        (await verify.Sessions.SingleAsync(Ct)).Status.Should().Be(SessionStatus.Scheduled);
        (await verify.Set<SessionCompletionFact>().CountAsync(Ct)).Should().Be(0);
        (await verify.Set<OutboxMessage>().CountAsync(Ct)).Should().Be(0);
    }
    [Fact]
    public async Task Two_workers_actually_wait_on_same_lifecycle_lock_and_create_one_fact()
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        Guid id = await Due(db);
        PauseSave pause = new();
        await using ServiceProvider services = Services(db, pause);
        await using AsyncServiceScope first = services.CreateAsyncScope();
        await using AsyncServiceScope second = services.CreateAsyncScope();
        ApplicationDbContext secondDb = second.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await secondDb.Database.OpenConnectionAsync(Ct);
        int pid = ((NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
        // Pretrack stale state to verify reload after arbitration.
        await secondDb.Sessions.SingleAsync(Ct);
        Task<bool> a = first.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id, Ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Task<bool> b = second.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id, Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using ApplicationDbContext monitor = db.NewContext();
            while (!await monitor.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid={pid} AND wait_event_type='Lock') AS \"Value\"").SingleAsync(timeout.Token))
                await Task.Delay(10, timeout.Token);
            b.IsCompleted.Should().BeFalse();
        }
        finally { pause.Release.TrySetResult(); }
        (await a).Should().BeTrue(); (await b).Should().BeFalse();
        await using ApplicationDbContext verify = db.NewContext();
        (await verify.Set<SessionCompletionFact>().CountAsync(Ct)).Should().Be(1);
        (await verify.Set<OutboxMessage>().CountAsync(Ct)).Should().Be(1);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unknown_end_or_cancelled_never_fabricates_fact(bool unknown)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        Guid id = await Due(db);
        await using (ApplicationDbContext context = db.NewContext())
        {
            if (unknown) await context.Database.ExecuteSqlRawAsync("UPDATE sessions SET end_time=NULL", Ct);
            else await context.Database.ExecuteSqlRawAsync("UPDATE sessions SET status=3", Ct);
        }
        await using ServiceProvider services = Services(db);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id, Ct)).Should().BeFalse();
        (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Set<OutboxMessage>().CountAsync(Ct)).Should().Be(0);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lifecycle_competitors_serialize_and_recheck(bool due, bool reschedule)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        Guid id;
        if (due) id = await Due(db);
        else
        {
            await using ApplicationDbContext seed = db.NewContext();
            Session session = db.NewSession(); id = session.Id.Value;
            seed.Sessions.Add(session); await seed.SaveChangesAsync(Ct);
        }
        PauseSave pause = new();
        await using ServiceProvider services = Services(db, pause);
        await using AsyncServiceScope first = services.CreateAsyncScope();
        await using AsyncServiceScope second = services.CreateAsyncScope();
        ApplicationDbContext a = first.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ApplicationDbContext b = second.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await b.Database.OpenConnectionAsync(Ct);
        int pid = ((NpgsqlConnection)b.Database.GetDbConnection()).ProcessID;
        await b.Sessions.SingleAsync(Ct); // deliberately stale graph
        async Task Mutate(ApplicationDbContext context)
        {
            if (reschedule)
            {
                var handler = new Cinema.Application.Sessions.Commands.RescheduleSession.RescheduleSessionCommandHandler(
                    new SessionSchedulingService(context, NSubstitute.Substitute.For<Cinema.Domain.Interfaces.IMovieInfoProvider>()), context);
                var result = await handler.Handle(new(id, DateTime.UtcNow.AddDays(4)), Ct);
                result.IsSuccess.Should().Be(!due);
            }
            else
            {
                var result = await new Cinema.Application.Sessions.Commands.CancelSession.CancelSessionCommandHandler(context).Handle(new(id), Ct);
                result.IsSuccess.Should().Be(!due);
            }
        }
        Task winner = due ? first.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id, Ct) : Mutate(a);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Task loser = due ? Mutate(b) : second.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id, Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using ApplicationDbContext monitor = db.NewContext();
            while (!await monitor.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid={pid} AND wait_event_type='Lock') AS \"Value\"").SingleAsync(timeout.Token))
                await Task.Delay(10, timeout.Token);
            loser.IsCompleted.Should().BeFalse();
        }
        finally {pause.Release.TrySetResult();}
        await Task.WhenAll(winner,loser);
        await using ApplicationDbContext verify = db.NewContext();
        Session final = await verify.Sessions.IgnoreQueryFilters().SingleAsync(Ct);
        final.Status.Should().Be(due ? SessionStatus.Ended : reschedule ? SessionStatus.Scheduled : SessionStatus.Cancelled);
        (await verify.Set<SessionCompletionFact>().CountAsync(Ct)).Should().Be(due ? 1 : 0);
        (await verify.Set<OutboxMessage>().CountAsync(Ct)).Should().Be(due ? 1 : 0);
    }

    [Fact]
    public async Task Scanner_advances_past_poison_and_across_batches_then_wraps()
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        Guid poison = Guid.Empty;
        for(int i=0;i<55;i++)
        {
            Guid id = await Due(db); if(i==0) poison=id;
        }
        await using(ApplicationDbContext context=db.NewContext())
        {
            // Database fault on a single source; no business BLOCKED state.
            await context.Database.ExecuteSqlRawAsync($"CREATE FUNCTION reject_completion() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.session_id = '{poison}'::uuid THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_completion BEFORE INSERT ON session_completion_facts FOR EACH ROW EXECUTE FUNCTION reject_completion();",Ct);
        }
        await using ServiceProvider services=Services(db);
        SessionCompletionJob job=new(services.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionCompletionJob>.Instance);
        await job.Process(Ct); await job.Process(Ct); await job.Process(Ct); await job.Process(Ct);
        await using ApplicationDbContext verify=db.NewContext();
        (await verify.Set<SessionCompletionFact>().CountAsync(Ct)).Should().Be(54);
        (await verify.Sessions.CountAsync(s=>s.Status==SessionStatus.Scheduled,Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Committed_outbox_recovers_in_fresh_publisher_and_wire_duplicates_keep_identity()
    {
        await using HallLayoutDatabase db=await HallLayoutDatabase.CreateAsync(template);
        Guid id=await Due(db);
        await using(ServiceProvider unavailable=Services(db))
        {
            await using AsyncServiceScope scope=unavailable.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id,Ct)).Should().BeTrue();
        }
        SessionCompletionFact fact;
        await using(ApplicationDbContext verify=db.NewContext()) fact=await verify.Set<SessionCompletionFact>().SingleAsync(Ct);
        ConcurrentQueue<(string Body,Guid? MessageId)> received=new();
        await using ServiceProvider restarted=ServicesCore(db,received);
        List<IHostedService> hosted=restarted.GetServices<IHostedService>().ToList();
        try
        {
            foreach(IHostedService service in hosted) await service.StartAsync(Ct);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(Ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while(received.Count<1) await Task.Delay(20,timeout.Token);
            var first=received.First(); first.MessageId.Should().Be(fact.EventId);
            first.Body.Should().Be(fact.Snapshot);
            // Manual repeat verifies wire/idempotency identity, not lost-ACK redelivery.
            var envelope=JsonSerializer.Deserialize<Cinema.Application.Common.Contracts.SessionCompletedEnvelope>(fact.Snapshot)!;
            ISendEndpoint endpoint=await restarted.GetRequiredService<IBus>().GetSendEndpoint(new Uri("queue:loyalty_ticket_purchased"));
            await endpoint.Send(envelope,send=>send.MessageId=fact.EventId,Ct);
            while(received.Count<2) await Task.Delay(20,timeout.Token);
            received.Should().OnlyContain(message=>message.Body==fact.Snapshot && message.MessageId==fact.EventId);
            await using AsyncServiceScope check=restarted.CreateAsyncScope();
            (await check.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id,Ct)).Should().BeFalse();
        }
        finally
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach(IHostedService service in hosted.AsEnumerable().Reverse()) await service.StopAsync(timeout.Token);
        }
        await using ApplicationDbContext final=db.NewContext();
        (await final.Set<SessionCompletionFact>().CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Ended_without_source_is_diagnosed_not_fabricated()
    {
        await using HallLayoutDatabase db=await HallLayoutDatabase.CreateAsync(template);
        Guid id=await Due(db);
        await using(ApplicationDbContext context=db.NewContext()) await context.Database.ExecuteSqlRawAsync("UPDATE sessions SET status=4",Ct);
        await using ServiceProvider services=Services(db);
        await using AsyncServiceScope scope=services.CreateAsyncScope();
        Func<Task> complete=async()=>await scope.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id,Ct);
        (await complete.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("SESSION_COMPLETION_SOURCE_MISSING");
        await using ApplicationDbContext verify=db.NewContext();
        (await verify.Set<SessionCompletionFact>().CountAsync(Ct)).Should().Be(0);
    }
    [Fact]
    public async Task Source_cannot_be_deleted_through_normal_EF_writes_and_unique_tuple_is_enforced()
    {
        await using HallLayoutDatabase db=await HallLayoutDatabase.CreateAsync(template);
        Guid id=await Due(db);
        await using ServiceProvider services=Services(db);
        await using(AsyncServiceScope scope=services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(id,Ct);
        await using ApplicationDbContext context=db.NewContext();
        SessionCompletionFact fact=await context.Set<SessionCompletionFact>().SingleAsync(Ct);
        context.Remove(fact);
        Func<Task> remove=()=>context.SaveChangesAsync(Ct);
        await remove.Should().ThrowAsync<InvalidOperationException>();
        context.ChangeTracker.Clear();
        Func<Task> duplicate=()=>context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO session_completion_facts SELECT {Guid.NewGuid()},session_id,revision,completed_at_utc,snapshot FROM session_completion_facts",Ct);
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task Reservation_losing_to_cancellation_cannot_commit_tickets_for_cancelled_session()
    {
        await using HallLayoutDatabase db=await HallLayoutDatabase.CreateAsync(template);
        Guid userId=Guid.NewGuid(); Guid id; Guid seat;
        await using(ApplicationDbContext seed=db.NewContext())
        {
            Session session=db.NewSession(); id=session.Id.Value;
            seed.Sessions.Add(session); seed.Users.Add(new User{Id=userId,UserName="reservation-proof"});
            await seed.SaveChangesAsync(Ct);
            seat=await seed.Seats.Select(x=>x.Id.Value).FirstAsync(Ct);
        }
        PauseSave pause=new();
        await using ApplicationDbContext cancellation=db.NewContext(pause);
        await using ApplicationDbContext reservation=db.NewContext();
        await reservation.Database.OpenConnectionAsync(Ct);
        int pid=((NpgsqlConnection)reservation.Database.GetDbConnection()).ProcessID;
        Task<Cinema.Domain.Shared.Result> cancel=new Cinema.Application.Sessions.Commands.CancelSession.CancelSessionCommandHandler(cancellation).Handle(new(id),Ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10),Ct);
        var calculator=Substitute.For<Cinema.Application.Common.Interfaces.IPriceCalculator>();
        calculator.CalculatePrice(Arg.Any<Pricing>(),Arg.Any<Cinema.Domain.Common.EntityId<SeatType>>(),Arg.Any<DateTime>()).Returns(100m);
        Task<Cinema.Domain.Shared.Result<Guid>> reserve=new OrderReservationService(reservation,calculator).ReserveOrderAsync(userId,id,[seat],Ct);
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(Ct);timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using ApplicationDbContext monitor=db.NewContext();
            while(!await monitor.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid={pid} AND wait_event_type='Lock') AS \"Value\"").SingleAsync(timeout.Token))
                await Task.Delay(10,timeout.Token);
            reserve.IsCompleted.Should().BeFalse();
        }
        finally {pause.Release.TrySetResult();}
        (await cancel).IsSuccess.Should().BeTrue();
        (await reserve).IsFailure.Should().BeTrue();
        (await reserve).Error.Code.Should().Be("Session.NotAvailable");
        await using ApplicationDbContext verify=db.NewContext();
        (await verify.Tickets.CountAsync(Ct)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_lock_timeout_returns_busy_without_writes(bool reschedule)
    {
        await using HallLayoutDatabase db=await HallLayoutDatabase.CreateAsync(template);
        Guid id;
        await using(ApplicationDbContext seed=db.NewContext())
        {
            Session session=db.NewSession();id=session.Id.Value;
            seed.Sessions.Add(session);await seed.SaveChangesAsync(Ct);
        }
        await using NpgsqlConnection blocker=new(db.ConnectionString);
        await blocker.OpenAsync(Ct);
        await using NpgsqlTransaction transaction=await blocker.BeginTransactionAsync(Ct);
        await using(NpgsqlCommand command=new("SELECT id FROM halls WHERE id=@id FOR UPDATE",blocker,transaction))
        {command.Parameters.AddWithValue("id",db.Hall.Id.Value);await command.ExecuteNonQueryAsync(Ct);}
        await using ApplicationDbContext context=db.NewContext();
        Cinema.Domain.Shared.Result result;
        if(reschedule)
            result=await new Cinema.Application.Sessions.Commands.RescheduleSession.RescheduleSessionCommandHandler(
                new SessionSchedulingService(context,Substitute.For<Cinema.Domain.Interfaces.IMovieInfoProvider>()),context).Handle(new(id,DateTime.UtcNow.AddDays(5)),Ct);
        else result=await new Cinema.Application.Sessions.Commands.CancelSession.CancelSessionCommandHandler(context).Handle(new(id),Ct);
        result.IsFailure.Should().BeTrue();result.Error.Code.Should().Be("Session.Busy");
        await using ApplicationDbContext verify=db.NewContext();
        (await verify.Sessions.SingleAsync(Ct)).Status.Should().Be(SessionStatus.Scheduled);
        (await verify.Set<SessionCompletionFact>().CountAsync(Ct)).Should().Be(0);
        (await verify.Set<OutboxMessage>().CountAsync(Ct)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EF_wrapped_lock_conflict_returns_busy_and_rolls_back(bool reschedule)
    {
        await using HallLayoutDatabase db=await HallLayoutDatabase.CreateAsync(template);
        Guid id;
        await using(ApplicationDbContext seed=db.NewContext())
        {
            Session session=db.NewSession();id=session.Id.Value;
            seed.Sessions.Add(session);await seed.SaveChangesAsync(Ct);
            await seed.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_lifecycle_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test lock failure' USING ERRCODE='55P03'; END $$; CREATE TRIGGER fail_lifecycle_write BEFORE UPDATE ON sessions FOR EACH ROW EXECUTE FUNCTION fail_lifecycle_write();",Ct);
        }
        await using ApplicationDbContext context=db.NewContext();
        Cinema.Domain.Shared.Result result;
        if(reschedule)
            result=await new Cinema.Application.Sessions.Commands.RescheduleSession.RescheduleSessionCommandHandler(
                new SessionSchedulingService(context,Substitute.For<Cinema.Domain.Interfaces.IMovieInfoProvider>()),context).Handle(new(id,DateTime.UtcNow.AddDays(5)),Ct);
        else result=await new Cinema.Application.Sessions.Commands.CancelSession.CancelSessionCommandHandler(context).Handle(new(id),Ct);
        result.Error.Code.Should().Be("Session.Busy");
        await using ApplicationDbContext verify=db.NewContext();
        (await verify.Sessions.SingleAsync(Ct)).Status.Should().Be(SessionStatus.Scheduled);
    }

    private static async Task<Guid> Due(HallLayoutDatabase db, bool inactive = false)
    {
        await using ApplicationDbContext context = db.NewContext();
        Session session = db.NewSession(); context.Sessions.Add(session); await context.SaveChangesAsync(Ct);
        DateTime end = DateTime.UtcNow.Date.AddDays(-1).AddTicks(1234560);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET start_time={end.AddHours(-2)},end_time={end},occupancy_end_time={end.AddMinutes(15)} WHERE id={session.Id.Value}", Ct);
        if (inactive) await context.Database.ExecuteSqlRawAsync("UPDATE halls SET is_active=false", Ct);
        return session.Id.Value;
    }
    private static ServiceProvider Services(HallLayoutDatabase db, params IInterceptor[] interceptors) => ServicesCore(db, null, interceptors);
    private static ServiceProvider ServicesCore(HallLayoutDatabase db, ConcurrentQueue<(string Body, Guid? MessageId)>? received, params IInterceptor[] interceptors)
    {
        string cs = db.ConnectionString;
        ServiceCollection services = new(); services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(cs, pg => {pg.UseVector(); pg.EnableRetryOnFailure(3);})
            .UseSnakeCaseNamingConvention().AddInterceptors(interceptors));
        services.AddScoped<SessionCompletionService>();
        services.AddMassTransit(x => {
            x.AddEntityFrameworkOutbox<ApplicationDbContext>(o => {o.UsePostgres(); o.UseBusOutbox();});
            x.UsingRabbitMq((_, bus) => {
                bus.Host("127.0.0.1", (ushort)(received is null ? 1 : 5672), "/", host => {
                    host.Username(Environment.GetEnvironmentVariable("CINEMA_TEST_RABBITMQ_USERNAME") ?? "cinema_test");
                    host.Password(Environment.GetEnvironmentVariable("CINEMA_TEST_RABBITMQ_PASSWORD") ?? "cinema_test_password");
                });
                bus.UseRawJsonSerializer();
                if (received is not null) bus.ReceiveEndpoint("completion-proof-"+Guid.NewGuid().ToString("N"), endpoint => {
                    endpoint.AutoDelete=true; endpoint.Durable=false;
                    endpoint.ConfigureConsumeTopology=false;
                    endpoint.Bind("loyalty_ticket_purchased");
                    endpoint.UseRawJsonDeserializer(RawSerializerOptions.All);
                    endpoint.Handler<Cinema.Application.Common.Contracts.SessionCompletedEnvelope>(message => {
                        received.Enqueue((message.Message.ToJson(),message.MessageId)); return Task.CompletedTask;
                    });
                });
            });
        });
        LogContext.ConfigureCurrentLogContext(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        return services.BuildServiceProvider(true);
    }
    private sealed class FailSave(bool afterSave) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken=default)
            => afterSave ? ValueTask.FromResult(result) : throw new IOException("Injected before save");
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken=default)
            => throw new IOException("Injected after DB save before commit");
    }
    private sealed class PauseSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered {get;} = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;} = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken=default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15),cancellationToken); return result; }
    }
}
