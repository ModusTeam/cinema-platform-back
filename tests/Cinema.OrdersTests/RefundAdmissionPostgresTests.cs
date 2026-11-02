using System.Data.Common;
using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Services;
using Cinema.Application.Sessions.Commands.RescheduleSession;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Interfaces;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public class RefundAdmissionPostgresTests(PostgresSchemaTemplate template) : IClassFixture<PostgresSchemaTemplate>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Concurrent_admissions_enforce_read_committed_despite_repeatable_read_connection_default()
    {
        await using Fixture f = await Fixture.Create(template); PauseSave pause = new();
        Task<Result<RefundOperation>> first = f.Admission(interceptors: [new RepeatableReadDefault(), pause])
            .ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        pause.Isolation.Should().Be("read committed");
        Task<Result<RefundOperation>> second = f.Admission(interceptors: [new RepeatableReadDefault()])
            .ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        try { await f.WaitForLock(); second.IsCompleted.Should().BeFalse(); }
        finally { pause.Release.TrySetResult(); }
        (await first).Value.Id.Should().Be((await second).Value.Id);
    }
    [Fact]
    public async Task Existing_nonordinary_approval_is_not_relabelled_as_customer_admission()
    {
        await using Fixture f = await Fixture.Create(template);
        RefundOperation candidate = RefundOperation.Create(Guid.NewGuid(), f.Order.Id.Value, f.Order.UserId, f.Order.SessionId.Value,
            f.Order.PaymentTransactionId!, 100, "USD", f.Order.PaidAt!.Value, await f.Now(), f.Order.UserId, "different admission", "{}");
        (await f.Store().PersistApprovedAsync(candidate, Ct)).IsSuccess.Should().BeTrue();
        (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).Error.Code.Should().Be("Refund.IdentityConflict");
    }

    [Fact]
    public async Task Admission_first_retains_its_valid_ticket_snapshot_before_later_ticket_use()
    {
        await using Fixture f = await Fixture.Create(template); PauseSave pause = new();
        Task<Result<RefundOperation>> approval = f.Admission(interceptors: [pause]).ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using ApplicationDbContext writer = f.Context();
        Task<int> change = writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE tickets SET ticket_status={(int)TicketStatus.Used} WHERE order_id={f.Order.Id.Value}", Ct);
        try { await f.WaitForLock(); change.IsCompleted.Should().BeFalse(); }
        finally { pause.Release.TrySetResult(); }
        RefundOperation op = (await approval).Value; await change;
        using JsonDocument evidence = JsonDocument.Parse(op.AdmissionEvidence);
        evidence.RootElement.GetProperty("tickets")[0].GetProperty("status").GetString().Should().Be("Valid");
        await using ApplicationDbContext verify = f.Context();
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Used);
        // Admission is historical evidence, not an activated reservation or monetary executor.
        (await verify.Set<RefundOperation>().SingleAsync(Ct)).State.Should().Be(RefundState.Approved);
    }
    [Fact]
    public async Task Historical_operation_with_unverified_currency_cannot_reach_provider_execution()
    {
        await using Fixture f = await Fixture.Create(template, currency: null);
        RefundOperation legacy = RefundOperation.Create(Guid.NewGuid(), f.Order.Id.Value, f.Order.UserId, f.Order.SessionId.Value,
            f.Order.PaymentTransactionId!, 100, "UAH", f.Order.PaidAt!.Value, await f.Now(), f.Order.UserId, "historical trusted instruction", "{}");
        await using (ApplicationDbContext seed = f.Context()) { seed.Add(legacy); await seed.SaveChangesAsync(Ct); }
        IRefundProvider provider = Substitute.For<IRefundProvider>();
        provider.SubmitAsync(Arg.Any<RefundProviderRequest>(), Arg.Any<CancellationToken>()).Returns(new RefundProviderOutcome(RefundOutcomeKind.Succeeded, "must-not-call"));
        (await new RefundExecutor(f.Store(), provider).ExecuteAsync(legacy.Id, Ct)).Should().Be(RefundState.ReconciliationRequired);
        await provider.DidNotReceiveWithAnyArgs().SubmitAsync(default!, Ct);
        (await f.Store().GetAsync(legacy.Id, Ct))!.ProviderKey.Should().Be(legacy.ProviderKey);
    }
    [Fact]
    public async Task Completed_gold_ownership_can_be_snapshotted_without_compensation_or_money_execution()
    {
        await using Fixture f = await Fixture.Create(template);
        await using ApplicationDbContext setup = f.Context();
        Guid ticketId = (await setup.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
        await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET gold_checkout_state={(int)GoldCheckoutState.Completed},gold_use_confirmed=true,gold_ticket_id={ticketId} WHERE id={f.Order.Id.Value}", Ct);
        await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE tickets SET is_gold_upgraded=true WHERE id={ticketId}", Ct);
        RefundOperation op = (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).Value;
        op.Amount.Should().Be(100); op.State.Should().Be(RefundState.Approved);
        (await setup.Orders.AsNoTracking().SingleAsync(Ct)).GoldCheckoutState.Should().Be(GoldCheckoutState.Completed);
    }

    [Fact]
    public async Task Approval_snapshots_authoritative_facts_and_replay_does_not_move_cutoff_or_identity()
    {
        await using Fixture f = await Fixture.Create(template);
        DateTime before = await f.Now();
        RefundOperation first = (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).Value;
        first.ApprovedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(await f.Now());
        first.Currency.Should().Be("USD"); first.Amount.Should().Be(100); first.ActorId.Should().Be(f.Order.UserId);
        first.ChargeReference.Should().Be(f.Order.PaymentTransactionId);
        using JsonDocument snapshot = JsonDocument.Parse(first.AdmissionEvidence);
        DateTime start = snapshot.RootElement.GetProperty("sessionStartTimeUtc").GetDateTime();
        start.Should().Be(f.Start);
        snapshot.RootElement.GetProperty("tickets").GetArrayLength().Should().Be(1);
        await using (ApplicationDbContext change = f.Context())
            await change.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET start_time=clock_timestamp()+interval '5 minutes' WHERE id={f.Order.SessionId.Value}", Ct);
        RefundOperation replay = (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).Value;
        replay.Id.Should().Be(first.Id); replay.ProviderKey.Should().Be(first.ProviderKey);
        replay.ApprovedAtUtc.Should().Be(first.ApprovedAtUtc); replay.AdmissionEvidence.Should().Be(first.AdmissionEvidence);
        await using ApplicationDbContext verify = f.Context();
        (await verify.Set<RefundOperation>().CountAsync(Ct)).Should().Be(1);
        (await verify.Orders.SingleAsync(Ct)).Status.Should().Be(OrderStatus.Paid);
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Fact]
    public async Task Unauthenticated_or_unrelated_principal_cannot_obtain_an_operation()
    {
        await using Fixture f = await Fixture.Create(template);
        (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).IsSuccess.Should().BeTrue();
        (await f.Admission(Guid.Empty).ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).Error.Code.Should().Be("Auth.Required");
        (await f.Admission(Guid.NewGuid()).ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).Error.Code.Should().Be("Order.NotFound");
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.PaymentProcessing)]
    [InlineData(OrderStatus.PaymentOutcomeUnknown)]
    public async Task Unconfirmed_states_cannot_create_monetary_approval(OrderStatus state)
    {
        await using Fixture f = await Fixture.Create(template, state: state);
        (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).IsFailure.Should().BeTrue();
        await f.AssertNoOperation();
    }

    [Theory]
    [InlineData("legacy", "Refund.CurrencyReconciliationRequired")]
    [InlineData("zero", "Refund.ZeroCash")]
    [InlineData("used", "Refund.TicketReconciliationRequired")]
    [InlineData("missing", "Refund.TicketReconciliationRequired")]
    [InlineData("gold", "Refund.GoldRecoveryRequired")]
    [InlineData("unknown", "Refund.PaymentReconciliationRequired")]
    [InlineData("gold-owner", "Refund.GoldRecoveryRequired")]
    [InlineData("amount", "Refund.SourceMismatch")]
    [InlineData("late", "Refund.TooLate")]
    public async Task Ineligible_sources_are_rejected_without_side_effects(string source, string code)
    {
        await using Fixture f = await Fixture.Create(template, currency: source == "legacy" ? null : "USD", cash: source == "zero" ? 0 : 100);
        await using ApplicationDbContext setup = f.Context();
        if (source == "used") await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE tickets SET ticket_status={(int)TicketStatus.Used} WHERE order_id={f.Order.Id.Value}", Ct);
        if (source == "missing") await setup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM tickets WHERE order_id={f.Order.Id.Value}", Ct);
        if (source == "gold") await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET gold_checkout_state={(int)GoldCheckoutState.CompensationPending} WHERE id={f.Order.Id.Value}", Ct);
        if (source == "unknown") await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET gold_payment_outcome_unknown=true WHERE id={f.Order.Id.Value}", Ct);
        if (source == "gold-owner") await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET gold_checkout_state={(int)GoldCheckoutState.Completed} WHERE id={f.Order.Id.Value}", Ct);
        if (source == "amount") await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET paid_amount=99 WHERE id={f.Order.Id.Value}", Ct);
        if (source == "late") await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET start_time=clock_timestamp()+interval '60 minutes' WHERE id={f.Order.SessionId.Value}", Ct);
        (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).Error.Code.Should().Be(code);
        await f.AssertNoOperation();
    }

    [Fact]
    public async Task Concurrent_request_identities_wait_for_order_lock_and_share_one_operation()
    {
        await using Fixture f = await Fixture.Create(template); PauseSave pause = new();
        Task<Result<RefundOperation>> first = f.Admission(interceptors: [pause]).ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Task<Result<RefundOperation>> second = f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        try { await f.WaitForLock(); second.IsCompleted.Should().BeFalse(); }
        finally { pause.Release.TrySetResult(); }
        RefundOperation a = (await first).Value, b = (await second).Value;
        a.Id.Should().Be(b.Id); a.ProviderKey.Should().Be(b.ProviderKey); a.ApprovedAtUtc.Should().Be(b.ApprovedAtUtc);
        await using ApplicationDbContext verify = f.Context();
        (await verify.Set<RefundOperation>().CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Failure_before_commit_rolls_back_approval_without_mutating_source()
    {
        await using Fixture f = await Fixture.Create(template);
        Func<Task> approve = () => f.Admission(interceptors: [new FailSave()]).ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        await approve.Should().ThrowAsync<IOException>(); await f.AssertNoOperation();
        await using ApplicationDbContext verify = f.Context();
        (await verify.Orders.SingleAsync(Ct)).Status.Should().Be(OrderStatus.Paid);
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Fact]
    public async Task Lost_commit_response_is_recovered_without_callback_replay_or_new_key()
    {
        await using Fixture f = await Fixture.Create(template); LostCommit fault = new();
        Guid request = Guid.NewGuid();
        Func<Task> approve = () => f.Admission(interceptors: [fault]).ApproveAsync(f.Order.Id.Value, request, Ct);
        await approve.Should().ThrowAsync<IOException>(); fault.Calls.Should().Be(1);
        await using ApplicationDbContext verify = f.Context();
        RefundOperation committed = await verify.Set<RefundOperation>().SingleAsync(Ct);
        RefundOperation recovered = (await f.Admission().ApproveAsync(f.Order.Id.Value, request, Ct)).Value;
        recovered.Id.Should().Be(committed.Id); recovered.ProviderKey.Should().Be(committed.ProviderKey);
        recovered.ApprovedAtUtc.Should().Be(committed.ApprovedAtUtc);
    }

    [Fact]
    public async Task Low_level_replay_cannot_rewrite_approval_time_or_identity()
    {
        await using Fixture f = await Fixture.Create(template);
        RefundOperation op = (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).Value;
        RefundOperation conflict = RefundOperation.Create(Guid.NewGuid(), op.OrderId.Value, op.UserId, op.SessionId, op.ChargeReference,
            op.Amount, op.Currency, op.PaidAtUtc, op.ApprovedAtUtc.AddMicroseconds(1), op.ActorId, op.Reason, op.AdmissionEvidence);
        (await f.Store().PersistApprovedAsync(conflict, Ct)).Error.Code.Should().Be("Refund.IdentityConflict");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Row_locked_source_changes_are_reloaded_before_approval(bool ticketChange)
    {
        await using Fixture f = await Fixture.Create(template);
        await using ApplicationDbContext stale = f.Context();
        (await stale.Orders.SingleAsync(Ct)).Status.Should().Be(OrderStatus.Paid);
        await using NpgsqlConnection writer = new(f.Connection); await writer.OpenAsync(Ct);
        await using NpgsqlTransaction tx = await writer.BeginTransactionAsync(Ct);
        string sql = ticketChange ? $"UPDATE tickets SET ticket_status={(int)TicketStatus.Used} WHERE order_id=@id" : $"UPDATE orders SET status={(int)OrderStatus.Cancelled} WHERE id=@id";
        await using (NpgsqlCommand update = new(sql, writer, tx)) { update.Parameters.AddWithValue("id", f.Order.Id.Value); await update.ExecuteNonQueryAsync(Ct); }
        Task<Result<RefundOperation>> approval = f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        await f.WaitForLock(); approval.IsCompleted.Should().BeFalse(); await tx.CommitAsync(Ct);
        (await approval).IsFailure.Should().BeTrue(); await f.AssertNoOperation();
    }

    [Fact]
    public async Task Lock_timeout_returns_busy_without_automatic_retry_or_operation()
    {
        await using Fixture f = await Fixture.Create(template);
        await using NpgsqlConnection writer = new(f.Connection); await writer.OpenAsync(Ct);
        await using NpgsqlTransaction tx = await writer.BeginTransactionAsync(Ct);
        await using (NpgsqlCommand command = new("SELECT id FROM sessions WHERE id=@id FOR UPDATE", writer, tx))
        { command.Parameters.AddWithValue("id", f.Order.SessionId.Value); await command.ExecuteNonQueryAsync(Ct); }
        (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).Error.Code.Should().Be("Refund.Busy");
        await tx.RollbackAsync(Ct); await f.AssertNoOperation();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_rescheduling_and_admission_share_session_arbitration(bool admissionFirst)
    {
        await using Fixture f = await Fixture.Create(template);
        PauseSave approvalPause = new(); PauseSessionLock lifecyclePause = new();
        await using ApplicationDbContext lifecycle = f.Context(admissionFirst ? [] : [lifecyclePause]);
        RescheduleSessionCommandHandler handler = new(new SessionSchedulingService(lifecycle, Substitute.For<IMovieInfoProvider>()), lifecycle);
        Task<Result> Reschedule() => handler.Handle(new RescheduleSessionCommand(f.Order.SessionId.Value, f.Start.AddDays(1)), Ct);
        Task<Result<RefundOperation>> approval;
        Task<Result> reschedule;
        if (admissionFirst)
        {
            approval = f.Admission(interceptors: [approvalPause]).ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
            await approvalPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            reschedule = Reschedule();
        }
        else
        {
            reschedule = Reschedule(); await lifecyclePause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            approval = f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        }
        try { await f.WaitForLock(); }
        finally { approvalPause.Release.TrySetResult(); lifecyclePause.Release.TrySetResult(); }
        (await reschedule).Error.Code.Should().Be("Session.CannotReschedule");
        RefundOperation op = (await approval).Value;
        using JsonDocument snapshot = JsonDocument.Parse(op.AdmissionEvidence);
        snapshot.RootElement.GetProperty("sessionStartTimeUtc").GetDateTime().Should().Be(f.Start);
    }

    private sealed class Fixture(string connection, string admin, string name, Order order, DateTime start) : IAsyncDisposable
    {
        public string Connection => connection; public Order Order => order; public DateTime Start => start;
        private DbContextOptions<ApplicationDbContext> Options(IInterceptor[] interceptors) => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connection, pg => { pg.UseVector(); pg.EnableRetryOnFailure(3); }).UseSnakeCaseNamingConvention().AddInterceptors(interceptors).Options;
        public ApplicationDbContext Context(params IInterceptor[] interceptors) => new(Options(interceptors));
        public RefundOperationStore Store(params IInterceptor[] interceptors) => new(new PooledDbContextFactory<ApplicationDbContext>(Options(interceptors)));
        public OrdinaryRefundAdmission Admission(Guid? actor = null, IInterceptor[]? interceptors = null)
        {
            ICurrentUserService user = Substitute.For<ICurrentUserService>(); Guid id = actor ?? order.UserId;
            user.UserId.Returns(id == Guid.Empty ? (Guid?)null : id);
            return new(Store(interceptors ?? []), user);
        }
        public async Task<DateTime> Now() { await using ApplicationDbContext db = Context(); return await db.Database.SqlQuery<DateTime>($"""SELECT clock_timestamp() AS "Value" """).SingleAsync(Ct); }
        public async Task AssertNoOperation() { await using ApplicationDbContext db = Context(); (await db.Set<RefundOperation>().CountAsync(Ct)).Should().Be(0); }
        public async Task WaitForLock()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using ApplicationDbContext monitor = Context();
            while (!await monitor.Database.SqlQuery<bool>($"""SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock') AS "Value" """).SingleAsync(timeout.Token)) await Task.Delay(10, timeout.Token);
        }
        public static async Task<Fixture> Create(PostgresSchemaTemplate template, string? currency = "USD", int cash = 100, OrderStatus state = OrderStatus.Paid)
        {
            var (connection, admin, name) = await template.CreateDatabaseAsync();
            await using ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection, p => p.UseVector()).UseSnakeCaseNamingConvention().Options);
            User user = new() { Id = Guid.NewGuid(), UserName = "admission-fixture" };
            Hall hall = Hall.Create(EntityId<Hall>.New(), "admission");
            Movie movie = Movie.CreateManual("admission", "test", 90, 2026, MovieStatus.ComingSoon);
            Pricing pricing = Pricing.New(EntityId<Pricing>.New(), "admission");
            DateTime start = DateTime.UtcNow.AddDays(2); start = start.AddTicks(-(start.Ticks % 10));
            Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2), movie.Id, hall.Id, pricing.Id);
            SeatType type = SeatType.New(EntityId<SeatType>.New(), "standard", null);
            Seat seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, type.Id);
            Order order = Order.New(EntityId<Order>.New(), 100, user.Id, session.Id);
            if (cash != 100) order.ApplyLoyaltyDiscount(100 - cash);
            if (state == OrderStatus.Paid) order.MarkAsPaid("admission-charge", currency);
            else if (state is OrderStatus.PaymentProcessing or OrderStatus.PaymentOutcomeUnknown) { order.StartPayment(); if (state == OrderStatus.PaymentOutcomeUnknown) order.MarkPaymentOutcomeUnknown(); }
            Ticket ticket = Ticket.New(EntityId<Ticket>.New(), 100, state == OrderStatus.Paid ? TicketStatus.Valid : TicketStatus.Reserved, order.Id, session.Id, seat.Id);
            db.AddRange(user, hall, movie, pricing, session, type, seat, order, ticket); await db.SaveChangesAsync(Ct);
            return new(connection, admin, name, order, start);
        }
        public async ValueTask DisposeAsync() { await using NpgsqlConnection db = new(admin); await db.OpenAsync(); await using NpgsqlCommand drop = new($"DROP DATABASE {name} WITH (FORCE)", db); await drop.ExecuteNonQueryAsync(); }
    }
    private sealed class PauseSave : SaveChangesInterceptor
    {
        public string? Isolation { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Isolation = await data.Context!.Database.SqlQuery<string>($"""SELECT current_setting('transaction_isolation') AS "Value" """).SingleAsync(cancellationToken);
            Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); return result;
        }
    }
    private sealed class RepeatableReadDefault : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData data, CancellationToken cancellationToken = default)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SET default_transaction_isolation = 'repeatable read'";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
    private sealed class PauseSessionLock : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken cancellationToken = default)
        { if (command.CommandText.Contains("sessions WHERE") && command.CommandText.Contains("FOR UPDATE")) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); } return result; }
    }
    private sealed class FailSave : SaveChangesInterceptor
    { public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw new IOException("Injected approval failure"); }
    private sealed class LostCommit : DbTransactionInterceptor
    {
        public int Calls { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken cancellationToken = default)
        { Calls++; throw new IOException("Lost approval commit response"); }
    }
}
