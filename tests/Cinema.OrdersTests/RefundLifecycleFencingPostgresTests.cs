using System.Data.Common;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public partial class RefundAdmissionPostgresTests
{
    [Fact]
    public async Task Validation_committed_first_is_reloaded_by_legacy_preflight_before_any_money_call()
    {
        await using Fixture f = await Fixture.Create(template); PauseSave pause = new();
        await using ApplicationDbContext validation = f.Context(pause);
        Guid id = (await validation.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
        Task<Result<string>> use = new ValidateTicketCommandHandler(validation, new ScreeningClock(f.Start)).Handle(new(id), Ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using ApplicationDbContext cancellation = f.Context(); IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        Task<Result> cancel = Legacy(f, cancellation, payment, loyalty).Handle(new(f.Order.Id.Value), Ct);
        try { await f.WaitForLock(); }
        finally { pause.Release.TrySetResult(); }
        (await use).IsSuccess.Should().BeTrue(); (await cancel).Error.Code.Should().Be("Order.InvalidState");
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
    }

    [Fact]
    public async Task Legacy_in_flight_without_durable_claim_still_allows_ticket_use_ACTIVATION_BLOCKER()
    {
        await using Fixture f = await Fixture.Create(template);
        await using ApplicationDbContext cancellation = f.Context(); IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); return PaymentResult.Success("legacy-refunded"); });
        Task<Result> cancel = Legacy(f, cancellation, payment, loyalty).Handle(new(f.Order.Id.Value), Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            await using ApplicationDbContext validation = f.Context();
            Guid id = (await validation.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
            (await new ValidateTicketCommandHandler(validation, new ScreeningClock(f.Start)).Handle(new(id), Ct)).IsSuccess.Should().BeTrue();
        }
        finally { release.TrySetResult(); }
        (await cancel).IsSuccess.Should().BeTrue();
        await payment.Received(1).RefundPaymentAsync(f.Order.PaymentTransactionId!, Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = f.Context();
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Refunded);
    }
    [Fact]
    public async Task Validation_cannot_be_wrapped_in_an_automatically_retried_callback()
    {
        await using Fixture f = await Fixture.Create(template);
        await using ApplicationDbContext context = f.Context();
        Guid id = (await context.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
        Func<Task> retry = () => context.Database.CreateExecutionStrategy().ExecuteAsync(
            () => new ValidateTicketCommandHandler(context, new ScreeningClock(f.Start)).Handle(new(id), Ct));
        await retry.Should().ThrowAsync<InvalidOperationException>();
        await using ApplicationDbContext verify = f.Context();
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Theory]
    [InlineData(TicketStatus.Used, "Ticket.Used")]
    [InlineData(TicketStatus.Refunded, "Ticket.Refunded")]
    [InlineData(TicketStatus.Reserved, "Ticket.NotPurchased")]
    [InlineData(TicketStatus.Released, "Ticket.NotPurchased")]
    public async Task Existing_ticket_status_errors_are_preserved(TicketStatus state, string code)
    {
        await using Fixture f = await Fixture.Create(template);
        await using ApplicationDbContext context = f.Context();
        Guid id = (await context.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE tickets SET ticket_status={(int)state} WHERE id={id}", Ct);
        (await new ValidateTicketCommandHandler(context, new ScreeningClock(f.Start)).Handle(new(id), Ct)).Error.Code.Should().Be(code);
    }

    [Fact]
    public async Task Missing_ticket_and_wrong_date_still_fail_without_consumption()
    {
        await using Fixture f = await Fixture.Create(template);
        await using ApplicationDbContext context = f.Context();
        Guid id = (await context.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
        ValidateTicketCommandHandler handler = new(context, new ScreeningClock(f.Start.AddDays(-1)));
        (await handler.Handle(new(Guid.NewGuid()), Ct)).Error.Code.Should().Be("Ticket.Invalid");
        (await handler.Handle(new(id), Ct)).Error.Code.Should().Be("Ticket.WrongDate");
        (await context.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Valid);
    }
    private sealed class ScreeningClock(DateTime screening) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(screening); }

    [Theory]
    [InlineData(RefundState.Approved)]
    [InlineData(RefundState.Dispatching)]
    [InlineData(RefundState.Pending)]
    [InlineData(RefundState.Unknown)]
    [InlineData(RefundState.ReconciliationRequired)]
    [InlineData(RefundState.Succeeded)]
    [InlineData(RefundState.Declined)]
    public async Task Refund_owned_ticket_cannot_be_used_even_after_expiry_or_definitive_decline(RefundState state)
    {
        await using Fixture f = await Fixture.Create(template);
        Guid id = (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).Value.Id;
        if (state != RefundState.Approved)
        {
            var claim = (await f.Store().ClaimAsync(id, Ct))!;
            if (state == RefundState.Dispatching)
            {
                await using ApplicationDbContext expiry = f.Context();
                await expiry.Database.ExecuteSqlInterpolatedAsync($"UPDATE refund_operations SET lease_expires_at_utc=clock_timestamp()-interval '1 second' WHERE id={id}", Ct);
            }
            else
            {
                var kind = state switch { RefundState.Succeeded => RefundOutcomeKind.Succeeded, RefundState.Declined => RefundOutcomeKind.Declined, RefundState.Pending => RefundOutcomeKind.Pending, _ => RefundOutcomeKind.Unknown };
                await f.Store().ObserveAsync(claim, new(kind, state == RefundState.Succeeded ? "confirmed-reference" : null), Ct);
                (await f.Store().ResolveAsync(claim, state, "fixture-evidence", Ct)).Should().BeTrue();
            }
        }
        await using ApplicationDbContext validation = f.Context();
        Ticket ticket = await validation.Tickets.IgnoreQueryFilters().SingleAsync(Ct);
        var result = await new ValidateTicketCommandHandler(validation, new ScreeningClock(f.Start)).Handle(new(ticket.Id.Value), Ct);
        result.Error.Code.Should().Be("Ticket.RefundResolutionRequired");
        await using ApplicationDbContext verify = f.Context();
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Valid);
        (await f.Store().GetAsync(id, Ct))!.State.Should().Be(state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_approval_and_validation_have_one_winner_under_overlapping_transactions(bool approvalFirst)
    {
        await using Fixture f = await Fixture.Create(template); PauseSave pause = new();
        await using ApplicationDbContext validation = f.Context(approvalFirst ? [] : [pause]);
        Ticket ticket = await validation.Tickets.IgnoreQueryFilters().SingleAsync(Ct);
        Task<Result<RefundOperation>> approval;
        Task<Result<string>> use;
        if (approvalFirst)
        {
            approval = f.Admission(interceptors: [pause]).ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            use = new ValidateTicketCommandHandler(validation, new ScreeningClock(f.Start)).Handle(new(ticket.Id.Value), Ct);
        }
        else
        {
            use = new ValidateTicketCommandHandler(validation, new ScreeningClock(f.Start)).Handle(new(ticket.Id.Value), Ct);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            approval = f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        }
        try { await f.WaitForLock(); }
        finally { pause.Release.TrySetResult(); }
        if (approvalFirst)
        {
            (await approval).IsSuccess.Should().BeTrue();
            (await use).Error.Code.Should().Be("Ticket.RefundResolutionRequired");
        }
        else
        {
            (await use).IsSuccess.Should().BeTrue();
            (await approval).Error.Code.Should().Be("Refund.TicketReconciliationRequired");
        }
    }

    [Fact]
    public async Task Two_actual_validation_requests_cannot_both_consume_one_ticket()
    {
        await using Fixture f = await Fixture.Create(template); InitialTicketReads barrier = new();
        await using ApplicationDbContext a = f.Context(barrier), b = f.Context(barrier);
        await using ApplicationDbContext lookup = f.Context();
        Guid id = (await lookup.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
        Task<Result<string>> first = new ValidateTicketCommandHandler(a, new ScreeningClock(f.Start)).Handle(new(id), Ct);
        Task<Result<string>> second = new ValidateTicketCommandHandler(b, new ScreeningClock(f.Start)).Handle(new(id), Ct);
        Result<string>[] results = await Task.WhenAll(first, second);
        barrier.Arrivals.Should().BeGreaterThanOrEqualTo(2);
        results.Count(x => x.IsSuccess).Should().Be(1);
        results.Single(x => x.IsFailure).Error.Code.Should().Be("Ticket.Used");
    }

    [Fact]
    public async Task Validation_save_failure_rolls_back_and_a_fresh_attempt_can_succeed()
    {
        await using Fixture f = await Fixture.Create(template);
        await using ApplicationDbContext lookup = f.Context(); Guid id = (await lookup.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
        await using ApplicationDbContext failed = f.Context(new FailSave());
        Func<Task> validate = () => new ValidateTicketCommandHandler(failed, new ScreeningClock(f.Start)).Handle(new(id), Ct);
        await validate.Should().ThrowAsync<IOException>();
        await using ApplicationDbContext fresh = f.Context();
        (await fresh.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Valid);
        (await new ValidateTicketCommandHandler(fresh, new ScreeningClock(f.Start)).Handle(new(id), Ct)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Validation_lost_commit_response_is_not_replayed_or_assumed_rolled_back()
    {
        await using Fixture f = await Fixture.Create(template); LostCommit fault = new();
        await using ApplicationDbContext validation = f.Context(fault);
        Guid id = (await validation.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
        Func<Task> validate = () => new ValidateTicketCommandHandler(validation, new ScreeningClock(f.Start)).Handle(new(id), Ct);
        await validate.Should().ThrowAsync<IOException>(); fault.Calls.Should().Be(1);
        await using ApplicationDbContext fresh = f.Context();
        (await new ValidateTicketCommandHandler(fresh, new ScreeningClock(f.Start)).Handle(new(id), Ct)).Error.Code.Should().Be("Ticket.Used");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_lock_timeout_causes_no_ticket_or_monetary_effect(bool cancellation)
    {
        await using Fixture f = await Fixture.Create(template);
        await using NpgsqlConnection owner = new(f.Connection); await owner.OpenAsync(Ct);
        await using NpgsqlTransaction tx = await owner.BeginTransactionAsync(Ct);
        await using (NpgsqlCommand command = new("SELECT id FROM orders WHERE id=@id FOR UPDATE", owner, tx))
        { command.Parameters.AddWithValue("id", f.Order.Id.Value); await command.ExecuteNonQueryAsync(Ct); }
        await using ApplicationDbContext context = f.Context(); IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        if (cancellation)
            (await Legacy(f, context, payment, loyalty).Handle(new(f.Order.Id.Value), Ct)).Error.Code.Should().Be("Order.Busy");
        else
        {
            Guid id = (await context.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
            (await new ValidateTicketCommandHandler(context, new ScreeningClock(f.Start)).Handle(new(id), Ct)).Error.Code.Should().Be("Ticket.Busy");
        }
        await tx.RollbackAsync(Ct);
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
        await using ApplicationDbContext verify = f.Context();
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Valid);
        (await verify.Orders.SingleAsync(Ct)).Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Approval_holds_ownership_before_legacy_provider_call_even_before_its_commit()
    {
        await using Fixture f = await Fixture.Create(template); PauseSave pause = new();
        Task<Result<RefundOperation>> approval = f.Admission(interceptors: [pause]).ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using ApplicationDbContext cancellation = f.Context(); IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(PaymentResult.Success("legacy-refund"));
        Task<Result> cancel = Legacy(f, cancellation, payment, loyalty).Handle(new(f.Order.Id.Value), Ct);
        try { await f.WaitForLock(); await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct); }
        finally { pause.Release.TrySetResult(); }
        (await approval).IsSuccess.Should().BeTrue();
        (await cancel).Error.Code.Should().Be("Order.RefundLifecycleOwned");
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
        await loyalty.DidNotReceiveWithAnyArgs().RefundPointsAsync(default, default, default, default!, Ct);
        await loyalty.DidNotReceiveWithAnyArgs().RollbackGoldUpgradeAsync(default, default, Ct);
    }

    [Fact]
    public async Task Committed_refund_operation_blocks_legacy_cancellation_and_unauthorized_calls()
    {
        await using Fixture f = await Fixture.Create(template, cash: 75);
        await using (ApplicationDbContext setup = f.Context())
        {
            Guid ticket = (await setup.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
            await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET gold_checkout_state={(int)GoldCheckoutState.Completed},gold_use_confirmed=true,gold_ticket_id={ticket} WHERE id={f.Order.Id.Value}", Ct);
            await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE tickets SET is_gold_upgraded=true WHERE id={ticket}", Ct);
        }
        (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).IsSuccess.Should().BeTrue();
        await using ApplicationDbContext context = f.Context(); IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(PaymentResult.Success("must-not-call"));
        loyalty.RefundPointsAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((true, string.Empty));
        loyalty.RollbackGoldUpgradeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((true, string.Empty));
        (await Legacy(f, context, payment, loyalty).Handle(new(f.Order.Id.Value), Ct)).Error.Code.Should().Be("Order.RefundLifecycleOwned");
        ICurrentUserService other = Substitute.For<ICurrentUserService>(); other.UserId.Returns(Guid.NewGuid());
        (await new CancelOrderCommandHandler(context, other, payment, loyalty).Handle(new(f.Order.Id.Value), Ct)).Error.Code.Should().Be("Order.NotFound");
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
        await loyalty.DidNotReceiveWithAnyArgs().RefundPointsAsync(default, default, default, default!, Ct);
        await loyalty.DidNotReceiveWithAnyArgs().RollbackGoldUpgradeAsync(default, default, Ct);
    }

    [Fact]
    public async Task Legacy_provider_call_already_in_flight_can_overlap_later_approval_ACTIVATION_BLOCKER()
    {
        await using Fixture f = await Fixture.Create(template);
        await using ApplicationDbContext context = f.Context(); IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); return PaymentResult.Success("mock-refunded"); });
        Task<Result> cancel = Legacy(f, context, payment, loyalty).Handle(new(f.Order.Id.Value), Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            // A real independent connection proves no Order row lock spans the remote call.
            await using NpgsqlConnection probe = new(f.Connection); await probe.OpenAsync(Ct);
            await using NpgsqlCommand command = new("SELECT id FROM orders WHERE id=@id FOR UPDATE NOWAIT", probe);
            command.Parameters.AddWithValue("id", f.Order.Id.Value); await command.ExecuteNonQueryAsync(Ct);
            (await f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct)).IsSuccess.Should().BeTrue();
        }
        finally { release.TrySetResult(); }
        (await cancel).IsSuccess.Should().BeTrue();
        await payment.Received(1).RefundPaymentAsync(f.Order.PaymentTransactionId!, Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = f.Context();
        (await verify.Orders.SingleAsync(Ct)).Status.Should().Be(OrderStatus.Cancelled);
        (await verify.Set<RefundOperation>().SingleAsync(Ct)).State.Should().Be(RefundState.Approved);
    }

    [Fact]
    public async Task Pending_cancellation_still_releases_without_financial_calls()
    {
        await using Fixture f = await Fixture.Create(template, state: OrderStatus.Pending);
        await using ApplicationDbContext context = f.Context(); IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        (await Legacy(f, context, payment, loyalty).Handle(new(f.Order.Id.Value), Ct)).IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = f.Context();
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Released);
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
        (await verify.Set<RefundOperation>().CountAsync(Ct)).Should().Be(0);
    }

    private static CancelOrderCommandHandler Legacy(Fixture f, ApplicationDbContext db, IPaymentService payment, ILoyaltyService loyalty)
    {
        ICurrentUserService user = Substitute.For<ICurrentUserService>(); user.UserId.Returns(f.Order.UserId);
        return new(db, user, payment, loyalty);
    }

    private sealed class InitialTicketReads : DbCommandInterceptor
    {
        private int arrivals;
        public int Arrivals => arrivals;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM tickets") && !command.CommandText.Contains("FOR UPDATE"))
            {
                int count = Interlocked.Increment(ref arrivals);
                if (count == 2) ready.TrySetResult();
                if (count <= 2) await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
}
