using System.Data.Common;
using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Common.Utils;
using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Cinema.Application.Common.Models.DomainEventNotification;
using Cinema.Domain.Events;
using Cinema.Infrastructure.Persistence.Interceptors;
using Cinema.Infrastructure.Services;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using FluentAssertions;
using Xunit;

namespace Cinema.OrdersTests;

public partial class RefundAdmissionPostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paid_claim_and_validation_commit_in_either_order_have_one_lifecycle_winner(bool claimFirst)
    {
        await using Fixture f = await Fixture.Create(template);
        PauseSave pause = new(); IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        await using ApplicationDbContext validation = f.Context(claimFirst ? [] : [pause]);
        Guid ticket = (await validation.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
        Task<Result> cancellation;
        Task<Result<string>> use;
        if (claimFirst)
        {
            cancellation = f.Cancellation(payment, loyalty, pause).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            use = new ValidateTicketCommandHandler(validation, new ScreeningClock(f.Start)).Handle(new(ticket), Ct);
        }
        else
        {
            use = new ValidateTicketCommandHandler(validation, new ScreeningClock(f.Start)).Handle(new(ticket), Ct);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            cancellation = f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct);
        }
        try { await f.WaitForLock(); }
        finally { pause.Release.TrySetResult(); }
        if (claimFirst)
        {
            (await cancellation).IsSuccess.Should().BeTrue();
            (await use).Error.Code.Should().BeOneOf("Ticket.RefundResolutionRequired", "Ticket.Refunded");
        }
        else
        {
            (await use).IsSuccess.Should().BeTrue(); (await cancellation).Error.Code.Should().Be("Order.InvalidState");
            await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
            await using ApplicationDbContext verify = f.Context();
            (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Used);
            (await verify.PaidCancellations.CountAsync(Ct)).Should().Be(0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paid_claim_and_ordinary_admission_commit_in_either_order_are_exclusive(bool claimFirst)
    {
        await using Fixture f = await Fixture.Create(template); PauseSave pause = new();
        IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        Task<Result> cancellation; Task<Result<RefundOperation>> admission;
        if (claimFirst)
        {
            cancellation = f.Cancellation(payment, loyalty, pause).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            admission = f.Admission().ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
        }
        else
        {
            admission = f.Admission(interceptors: [pause]).ApproveAsync(f.Order.Id.Value, Guid.NewGuid(), Ct);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            cancellation = f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct);
        }
        try { await f.WaitForLock(); }
        finally { pause.Release.TrySetResult(); }
        if (claimFirst) { (await cancellation).IsSuccess.Should().BeTrue(); (await admission).Error.Code.Should().Be("Refund.CancellationOwned"); }
        else
        {
            (await admission).IsSuccess.Should().BeTrue(); (await cancellation).Error.Code.Should().Be("Order.RefundLifecycleOwned");
            await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
        }
        await using ApplicationDbContext verify = f.Context();
        ((await verify.PaidCancellations.CountAsync(Ct)) + (await verify.RefundOperations.CountAsync(Ct))).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_paid_cancellations_share_one_owner_and_one_dispatch()
    {
        await using Fixture f = await Fixture.Create(template); PauseSave pause = new();
        IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); return PaymentResult.Success("only-refund"); });
        Task<Result> first = f.Cancellation(payment, loyalty, pause).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Task<Result> second = f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct);
        try { await f.WaitForLock(); }
        finally { pause.Release.TrySetResult(); }
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            Result loser = await (first.IsCompleted ? first : second.IsCompleted ? second : Task.WhenAny(first, second).Unwrap());
            loser.Error.Code.Should().Be("Order.Busy");
        }
        finally { release.TrySetResult(); }
        Result[] results = await Task.WhenAll(first, second);
        results.Count(x => x.IsSuccess).Should().Be(1);
        await payment.Received(1).RefundPaymentAsync(f.Order.PaymentTransactionId!, Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = f.Context();
        (await verify.PaidCancellations.CountAsync(Ct)).Should().Be(1);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("generic-failure")]
    [InlineData("empty-success")]
    [InlineData("timeout")]
    public async Task Uncertain_legacy_refund_retains_owner_and_never_redispatches(string outcome)
    {
        await using Fixture f = await Fixture.Create(template);
        IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (outcome == "throw") throw new IOException("disconnected after possible effect");
            if (outcome == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
            return outcome == "empty-success" ? PaymentResult.Success("") : PaymentResult.Failure("unclassified outcome");
        });
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).Error.Code.Should().Be("Payment.RefundReconciliationRequired");
        await using ApplicationDbContext verify = f.Context();
        PaidCancellation original = await verify.PaidCancellations.AsNoTracking().SingleAsync(Ct);
        original.MoneyState.Should().Be(CancellationStepState.Unknown); original.RefundReference.Should().BeNull();
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).Error.Code.Should().Be("Payment.RefundReconciliationRequired");
        (await verify.PaidCancellations.AsNoTracking().SingleAsync(Ct)).Id.Should().Be(original.Id);
        await payment.Received(1).RefundPaymentAsync(f.Order.PaymentTransactionId!, Arg.Any<CancellationToken>());
        (await verify.Orders.SingleAsync(Ct)).Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Restart_after_claim_resumes_same_owner_without_fabricating_a_previous_attempt()
    {
        await using Fixture f = await Fixture.Create(template);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(Ct); CancelAfterClaim stop = new(stopped);
        IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        (await f.Cancellation(payment, loyalty, stop).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, stopped.Token)).IsFailure.Should().BeTrue();
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
        await using ApplicationDbContext verify = f.Context(); PaidCancellation before = await verify.PaidCancellations.AsNoTracking().SingleAsync(Ct);
        before.MoneyState.Should().Be(CancellationStepState.Ready);
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsSuccess.Should().BeTrue();
        (await verify.PaidCancellations.AsNoTracking().SingleAsync(Ct)).Id.Should().Be(before.Id);
        await payment.Received(1).RefundPaymentAsync(f.Order.PaymentTransactionId!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restart_after_uncertain_money_intent_never_assumes_no_external_effect_or_reissues_money()
    {
        await using Fixture f = await Fixture.Create(template); LostPaidCommit fault = new("money-intent");
        IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        (await f.Cancellation(payment, loyalty, fault).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).Error.Code.Should().Be("Order.CancellationReconciliationRequired");
        await using ApplicationDbContext db = f.Context(); PaidCancellation interrupted = await db.PaidCancellations.AsNoTracking().SingleAsync(Ct);
        interrupted.MoneyState.Should().Be(CancellationStepState.Dispatching);
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).Error.Code.Should().Be("Payment.RefundReconciliationRequired");
        PaidCancellation retained = await db.PaidCancellations.AsNoTracking().SingleAsync(Ct);
        retained.Id.Should().Be(interrupted.Id); retained.MoneyState.Should().Be(CancellationStepState.Unknown);
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
    }

    [Theory]
    [InlineData(false, 5, false)]
    [InlineData(false, 20, true)]
    [InlineData(false, 60, true)]
    [InlineData(true, 5, true)]
    public async Task Paid_claim_retains_ten_minute_customer_deadline_and_existing_admin_bypass(bool admin, int minutes, bool succeeds)
    {
        await using Fixture f = await Fixture.Create(template); IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        await using (ApplicationDbContext setup = f.Context())
            await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET start_time=clock_timestamp()+make_interval(mins=>{minutes})", Ct);
        Result result = await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, admin, Ct);
        result.IsSuccess.Should().Be(succeeds);
        if (!succeeds)
        {
            result.Error.Code.Should().Be("Order.TooLate");
            await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
            await using ApplicationDbContext verify = f.Context(); (await verify.PaidCancellations.CountAsync(Ct)).Should().Be(0);
        }
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("money-success")]
    [InlineData("finalization")]
    public async Task Lost_commit_acknowledgement_uses_fresh_identity_query_and_does_not_repeat_money(string phase)
    {
        await using Fixture f = await Fixture.Create(template); LostPaidCommit fault = new(phase);
        IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        (await f.Cancellation(payment, loyalty, fault).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsSuccess.Should().BeTrue();
        fault.Injections.Should().Be(1);
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsSuccess.Should().BeTrue();
        await payment.Received(1).RefundPaymentAsync(f.Order.PaymentTransactionId!, Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = f.Context(); (await verify.PaidCancellations.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Committed_money_success_survives_finalization_failure_and_retry_uses_fresh_tickets()
    {
        await using Fixture f = await Fixture.Create(template); FailPaidFinalization fault = new();
        IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        (await f.Cancellation(payment, loyalty, fault).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).Error.Code.Should().Be("Order.CancellationReconciliationRequired");
        await using ApplicationDbContext verify = f.Context();
        PaidCancellation evidence = await verify.PaidCancellations.AsNoTracking().SingleAsync(Ct);
        evidence.MoneyState.Should().Be(CancellationStepState.Succeeded); evidence.RefundReference.Should().Be("durable-refund");
        (await verify.Orders.AsNoTracking().SingleAsync(Ct)).Status.Should().Be(OrderStatus.Paid);
        (await verify.Tickets.AsNoTracking().IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Valid);
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsSuccess.Should().BeTrue();
        await payment.Received(1).RefundPaymentAsync(f.Order.PaymentTransactionId!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostgreSql_finalization_failure_rolls_back_projections_but_keeps_success_for_recovery()
    {
        await using Fixture f = await Fixture.Create(template); IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        await using ApplicationDbContext db = f.Context();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_paid_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected PostgreSQL finalization failure'; END $$;
            CREATE TRIGGER reject_paid_finalization BEFORE UPDATE OF ticket_status ON tickets
              FOR EACH ROW WHEN (NEW.ticket_status=2) EXECUTE FUNCTION reject_paid_finalization();
            """, Ct);
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).Error.Code.Should().Be("Order.CancellationReconciliationRequired");
        PaidCancellation observed = await db.PaidCancellations.AsNoTracking().SingleAsync(Ct);
        observed.MoneyState.Should().Be(CancellationStepState.Succeeded); observed.RefundReference.Should().Be("durable-refund"); observed.FinalizedAtUtc.Should().BeNull();
        (await db.Orders.AsNoTracking().SingleAsync(Ct)).Status.Should().Be(OrderStatus.Paid);
        (await db.Tickets.AsNoTracking().IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Valid);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_paid_finalization ON tickets; DROP FUNCTION reject_paid_finalization()", Ct);
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsSuccess.Should().BeTrue();
        await payment.Received(1).RefundPaymentAsync(f.Order.PaymentTransactionId!, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_compensation_recovery_retains_money_and_completed_steps_with_original_loyalty_identity(bool goldFails)
    {
        await using Fixture f = await Fixture.Create(template, cash: 75); await SetGold(f);
        IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        string key = DeterministicGuid.Create($"refund-{f.Order.Id.Value}").ToString();
        loyalty.RefundPointsAsync(f.Order.UserId, 25, f.Order.Id.Value, key, Arg.Any<CancellationToken>())
            .Returns(goldFails ? (true, "") : (false, "unknown"), (true, ""));
        loyalty.RollbackGoldUpgradeAsync(f.Order.UserId, f.Order.Id.Value, Arg.Any<CancellationToken>())
            .Returns(goldFails ? (false, "unknown") : (true, ""), (true, ""));
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).Error.Code
            .Should().Be(goldFails ? "Loyalty.GoldRollbackFailed" : "Loyalty.RefundFailed");
        await using ApplicationDbContext verify = f.Context(); PaidCancellation before = await verify.PaidCancellations.AsNoTracking().SingleAsync(Ct);
        before.MoneyState.Should().Be(CancellationStepState.Succeeded);
        before.PointsState.Should().Be(goldFails ? CancellationStepState.Succeeded : CancellationStepState.Dispatching);
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsSuccess.Should().BeTrue();
        await payment.Received(1).RefundPaymentAsync(f.Order.PaymentTransactionId!, Arg.Any<CancellationToken>());
        await loyalty.Received(goldFails ? 1 : 2).RefundPointsAsync(f.Order.UserId, 25, f.Order.Id.Value, key, Arg.Any<CancellationToken>());
        await loyalty.Received(goldFails ? 2 : 1).RollbackGoldUpgradeAsync(f.Order.UserId, f.Order.Id.Value, Arg.Any<CancellationToken>());
        (await verify.PaidCancellations.AsNoTracking().SingleAsync(Ct)).Id.Should().Be(before.Id);
    }

    [Fact]
    public async Task Zero_cash_cancellation_compensates_points_and_gold_without_inventing_money_or_currency()
    {
        await using Fixture f = await Fixture.Create(template, currency: null, cash: 0); await SetGold(f);
        IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.RefundPointsAsync(f.Order.UserId, 100, f.Order.Id.Value, DeterministicGuid.Create($"refund-{f.Order.Id.Value}").ToString(), Arg.Any<CancellationToken>()).Returns((true, ""));
        loyalty.RollbackGoldUpgradeAsync(f.Order.UserId, f.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsSuccess.Should().BeTrue();
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
        await using ApplicationDbContext verify = f.Context(); PaidCancellation op = await verify.PaidCancellations.SingleAsync(Ct);
        op.PaidAmount.Should().Be(0); op.PaidCurrency.Should().BeNull(); op.MoneyState.Should().Be(CancellationStepState.NotRequired);
        (await verify.RefundOperations.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Unknown_currency_is_retained_as_null_and_unrelated_caller_cannot_discover_claim()
    {
        await using Fixture f = await Fixture.Create(template, currency: null); IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).Error.Code.Should().Be("Order.CurrencyReconciliationRequired");
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, Guid.NewGuid(), false, Ct)).Error.Code.Should().Be("Order.NotFound");
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, null, false, Ct)).Error.Code.Should().Be("Auth.Required");
        await using ApplicationDbContext verify = f.Context(); PaidCancellation op = await verify.PaidCancellations.SingleAsync(Ct);
        op.PaidCurrency.Should().BeNull();
        using JsonDocument snapshot = JsonDocument.Parse(op.SourceSnapshot); snapshot.RootElement.GetProperty("PaidCurrency").ValueKind.Should().Be(JsonValueKind.Null);
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, Ct);
    }

    [Fact]
    public async Task Actual_handler_replays_completed_owner_and_keeps_original_cancelled_error_without_owner()
    {
        await using Fixture f = await Fixture.Create(template); IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        await using (ApplicationDbContext first = f.Context())
            (await Legacy(f, first, payment, loyalty).Handle(new(f.Order.Id.Value), Ct)).IsSuccess.Should().BeTrue();
        await using (ApplicationDbContext replay = f.Context())
            (await Legacy(f, replay, payment, loyalty).Handle(new(f.Order.Id.Value), Ct)).IsSuccess.Should().BeTrue();
        await payment.Received(1).RefundPaymentAsync(f.Order.PaymentTransactionId!, Arg.Any<CancellationToken>());
        await using Fixture old = await Fixture.Create(template, state: OrderStatus.Pending);
        await using (ApplicationDbContext setup = old.Context())
        { Order order = await setup.Orders.SingleAsync(Ct); order.MarkAsCancelled(); await setup.SaveChangesAsync(Ct); }
        await using ApplicationDbContext historical = old.Context();
        (await Legacy(old, historical, payment, loyalty).Handle(new(old.Order.Id.Value), Ct)).Error.Code.Should().Be("Order.AlreadyCancelled");
    }

    [Fact]
    public async Task Cancelled_request_does_not_discard_observed_success_and_postcommit_runs_after_transaction_disposal()
    {
        await using Fixture f = await Fixture.Create(template);
        using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        { disconnected.Cancel(); return PaymentResult.Success("success-after-disconnect"); });
        IPublisher publisher = Substitute.For<IPublisher>(); int notifications = 0; bool postCommitReadVerified = false;
        publisher.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (call.ArgAt<INotification>(0) is not DomainEventNotification<OrderCancelledDomainEvent>) return;
            notifications++;
            // Independent connection proves the finalization transaction is gone.
            await using NpgsqlConnection probe = new(f.Connection); await probe.OpenAsync(Ct);
            await using NpgsqlCommand command = new("SELECT id FROM orders WHERE id=@id FOR UPDATE NOWAIT", probe);
            command.Parameters.AddWithValue("id", f.Order.Id.Value); await command.ExecuteNonQueryAsync(Ct);
            await using ApplicationDbContext read = f.Context();
            (await read.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Refunded);
            postCommitReadVerified = true;
        });
        ServiceCollection services = new(); services.AddSingleton(publisher);
        services.AddScoped<DispatchDomainEventsInterceptor>();
        services.AddScoped<IPostCommitDomainEventDispatcher>(sp => sp.GetRequiredService<DispatchDomainEventsInterceptor>());
        PhaseContexts contexts = new();
        services.AddDbContext<ApplicationDbContext>((sp, b) => b.UseNpgsql(f.Connection, p => p.UseVector()).UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<DispatchDomainEventsInterceptor>(), contexts));
        await using ServiceProvider provider = services.BuildServiceProvider();
        PaidOrderCancellation cancellation = new(provider.GetRequiredService<IServiceScopeFactory>(), payment, loyalty);
        (await cancellation.CancelAsync(f.Order.Id.Value, f.Order.UserId, false, disconnected.Token)).IsSuccess.Should().BeTrue();
        notifications.Should().Be(1);
        postCommitReadVerified.Should().BeTrue("post-commit callback errors must not be hidden by notification isolation");
        contexts.ContextIds.Should().HaveCountGreaterThan(3).And.OnlyHaveUniqueItems();
        await using ApplicationDbContext verify = f.Context();
        (await verify.PaidCancellations.SingleAsync(Ct)).RefundReference.Should().Be("success-after-disconnect");
    }

    [Fact]
    public async Task Finalization_reloads_true_ticket_state_and_cannot_overwrite_used_with_refunded()
    {
        await using Fixture f = await Fixture.Create(template); IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await using ApplicationDbContext change = f.Context();
            await change.Database.ExecuteSqlRawAsync("UPDATE tickets SET ticket_status=1", Ct);
            return PaymentResult.Success("money-observed-before-stale-source");
        });
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).Error.Code.Should().Be("Order.CancellationReconciliationRequired");
        await using ApplicationDbContext verify = f.Context();
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Used);
        (await verify.PaidCancellations.SingleAsync(Ct)).RefundReference.Should().Be("money-observed-before-stale-source");
        (await verify.Orders.SingleAsync(Ct)).Status.Should().Be(OrderStatus.Paid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finalization_rejects_changed_original_price_or_gold_ownership_even_when_totals_match(bool gold)
    {
        await using Fixture f = await Fixture.Create(template);
        if (gold) await SetGold(f);
        IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.RollbackGoldUpgradeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((true, ""));
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await using ApplicationDbContext change = f.Context();
            if (gold) await change.Database.ExecuteSqlRawAsync("UPDATE orders SET gold_use_confirmed=false", Ct);
            else await change.Database.ExecuteSqlRawAsync("UPDATE orders SET total_amount=200; UPDATE tickets SET price_snapshot=200", Ct);
            return PaymentResult.Success("retained-success");
        });
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).Error.Code.Should().Be("Order.CancellationReconciliationRequired");
        await using ApplicationDbContext verify = f.Context();
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).TicketStatus.Should().Be(TicketStatus.Valid);
        (await verify.PaidCancellations.SingleAsync(Ct)).RefundReference.Should().Be("retained-success");
    }

    [Theory]
    [InlineData("{\"id\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"paid_amount\":-1}")]
    [InlineData("{\"paid_currency\":\"usd\"}")]
    [InlineData("{\"points_state\":\"Succeeded\"}")]
    [InlineData("{\"gold_state\":\"Succeeded\"}")]
    [InlineData("{\"refund_reference\":null}")]
    [InlineData("{\"source_snapshot\":\"[]\"}")]
    [InlineData("{\"execution_token\":\"11111111-1111-1111-1111-111111111111\"}")]
    public async Task PostgreSql_constraints_reject_incoherent_or_fabricated_journal(string changes)
    {
        await using Fixture f = await Fixture.Create(template);
        (await f.Cancellation(SuccessfulPayment(), Substitute.For<ILoyaltyService>()).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsSuccess.Should().BeTrue();
        await using ApplicationDbContext db = f.Context();
        Func<Task> insert = () => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO paid_cancellations SELECT (jsonb_populate_record(NULL::paid_cancellations, to_jsonb(p) || CAST({changes} AS jsonb))).* FROM paid_cancellations p", Ct);
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PostgreSql_trigger_arbitrates_direct_cancellation_and_refund_inserts_in_both_orders(bool cancellationFirst, bool repeatableRead)
    {
        await using Fixture f = await Fixture.Create(template);
        await using ApplicationDbContext first = PlainContext(f.Connection), second = PlainContext(f.Connection);
        PaidCancellation cancellation = PaidCancellation.Claim(Guid.NewGuid(), f.Order, f.Order.UserId, false, null, "{}", await f.Now());
        RefundOperation refund = RefundOperation.Create(Guid.NewGuid(), f.Order.Id.Value, f.Order.UserId, f.Order.SessionId.Value,
            f.Order.PaymentTransactionId!, f.Order.PaidAmount, "USD", f.Order.PaidAt!.Value, await f.Now(), f.Order.UserId, "test direct admission", "{}");
        await using var tx = await first.Database.BeginTransactionAsync(Ct);
        await using var secondTx = await second.Database.BeginTransactionAsync(repeatableRead ? System.Data.IsolationLevel.RepeatableRead : System.Data.IsolationLevel.ReadCommitted, Ct);
        // Establish a snapshot before the other owner exists, including at RR.
        _ = await second.PaidCancellations.CountAsync(Ct);
        _ = await second.RefundOperations.CountAsync(Ct);
        if (cancellationFirst) { first.Add(cancellation); second.Add(refund); }
        else { first.Add(refund); second.Add(cancellation); }
        await first.SaveChangesAsync(Ct);
        Task pending = second.SaveChangesAsync(Ct);
        try { await f.WaitForLock(); pending.IsCompleted.Should().BeFalse(); }
        finally { await tx.CommitAsync(Ct); }
        Func<Task> write = () => pending;
        // Npgsql's non-retrying strategy wraps a serialization failure in an
        // InvalidOperationException; assert the actual PostgreSQL failure.
        Exception failure = (await write.Should().ThrowAsync<Exception>()).Which;
        failure.GetBaseException().Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(repeatableRead ? "40001" : "23505");
    }

    [Theory]
    [InlineData("source_snapshot='{}'")]
    [InlineData("paid_currency='UAH'")]
    [InlineData("refund_reference='replaced'")]
    [InlineData("money_state='Ready',refund_reference=NULL")]
    [InlineData("finalized_at_utc=NULL")]
    public async Task PostgreSql_retains_original_source_and_success_evidence(string assignment)
    {
        await using Fixture f = await Fixture.Create(template); IPaymentService payment = SuccessfulPayment(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsSuccess.Should().BeTrue();
        await using ApplicationDbContext db = f.Context();
        // Closed test-only inputs exercise the actual trigger, not EF guards.
        Func<Task> mutate = () => db.Database.ExecuteSqlRawAsync("UPDATE paid_cancellations SET " + assignment.Replace("{", "{{").Replace("}", "}}"), Ct);
        (await mutate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0001");
        (await db.PaidCancellations.SingleAsync(Ct)).RefundReference.Should().Be("durable-refund");
    }

    [Fact]
    public async Task Low_level_refund_store_cannot_create_second_money_owner_and_preserves_existing_approval_evidence()
    {
        await using Fixture f = await Fixture.Create(template); IPaymentService payment = Substitute.For<IPaymentService>(); ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(PaymentResult.Failure("unknown"));
        (await f.Cancellation(payment, loyalty).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsFailure.Should().BeTrue();
        RefundOperation candidate = RefundOperation.Create(Guid.NewGuid(), f.Order.Id.Value, f.Order.UserId, f.Order.SessionId.Value,
            f.Order.PaymentTransactionId!, f.Order.PaidAmount, "USD", f.Order.PaidAt!.Value, await f.Now(), f.Order.UserId, "explicit approval", "{}");
        (await f.Store().PersistApprovedAsync(candidate, Ct)).Error.Code.Should().Be("Refund.CancellationOwned");
        await using Fixture other = await Fixture.Create(template);
        RefundOperation approved = (await other.Admission().ApproveAsync(other.Order.Id.Value, Guid.NewGuid(), Ct)).Value;
        (await other.Cancellation(payment, loyalty).CancelAsync(other.Order.Id.Value, other.Order.UserId, false, Ct)).Error.Code.Should().Be("Order.RefundLifecycleOwned");
        RefundOperation retained = (await other.Store().GetAsync(approved.Id, Ct))!;
        retained.AdmissionEvidence.Should().Be(approved.AdmissionEvidence); retained.ProviderKey.Should().Be(approved.ProviderKey);
        retained.State.Should().Be(RefundState.Approved);
    }

    [Fact]
    public async Task Ef_guards_retain_source_and_success_evidence_before_sending_any_update()
    {
        await using Fixture f = await Fixture.Create(template);
        (await f.Cancellation(SuccessfulPayment(), Substitute.For<ILoyaltyService>()).CancelAsync(f.Order.Id.Value, f.Order.UserId, false, Ct)).IsSuccess.Should().BeTrue();
        await using (ApplicationDbContext source = f.Context())
        {
            PaidCancellation op = await source.PaidCancellations.SingleAsync(Ct);
            source.Entry(op).Property(x => x.PaidCurrency).CurrentValue = "UAH";
            Func<Task> save = () => source.SaveChangesAsync(Ct);
            await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*source evidence are immutable*");
        }
        await using (ApplicationDbContext outcome = f.Context())
        {
            PaidCancellation op = await outcome.PaidCancellations.SingleAsync(Ct);
            outcome.Entry(op).Property(x => x.MoneyState).CurrentValue = CancellationStepState.Ready;
            Func<Task> save = () => outcome.SaveChangesAsync(Ct);
            await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*compensation is retained*");
        }
        await using ApplicationDbContext reference = f.Context();
        PaidCancellation successful = await reference.PaidCancellations.SingleAsync(Ct);
        reference.Entry(successful).Property(x => x.RefundReference).CurrentValue = "replacement";
        Func<Task> replace = () => reference.SaveChangesAsync(Ct);
        await replace.Should().ThrowAsync<InvalidOperationException>().WithMessage("*evidence is retained*");
    }

    private static ApplicationDbContext PlainContext(string connection) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql(connection, p => p.UseVector()).UseSnakeCaseNamingConvention().Options);
    private sealed class PhaseContexts : SaveChangesInterceptor
    {
        public List<Guid> ContextIds { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { ContextIds.Add(data.Context!.ContextId.InstanceId); return ValueTask.FromResult(result); }
    }

    private static IPaymentService SuccessfulPayment()
    {
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.RefundPaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(PaymentResult.Success("durable-refund")); return payment;
    }
    private static async Task SetGold(Fixture f)
    {
        await using ApplicationDbContext db = f.Context(); Guid ticket = (await db.Tickets.IgnoreQueryFilters().SingleAsync(Ct)).Id.Value;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET gold_checkout_state={(int)GoldCheckoutState.Completed},gold_use_confirmed=true,gold_ticket_id={ticket} WHERE id={f.Order.Id.Value}", Ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE tickets SET is_gold_upgraded=true WHERE id={ticket}", Ct);
    }
    private sealed class CancelAfterClaim(CancellationTokenSource stopped) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<PaidCancellation>().Any(x => x.Entity.ExecutionToken is null && x.Entity.FinalizedAtUtc is null)) stopped.Cancel();
            return Task.CompletedTask;
        }
    }
    private sealed class LostPaidCommit(string phase) : DbTransactionInterceptor
    {
        public int Injections { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken cancellationToken = default)
        {
            PaidCancellation? op = data.Context!.ChangeTracker.Entries<PaidCancellation>().SingleOrDefault()?.Entity;
            bool matches = phase switch { "claim" => op?.ExecutionToken is null && op?.MoneyState == CancellationStepState.Ready,
                "money-intent" => op?.MoneyState == CancellationStepState.Dispatching,
                "money-success" => op?.MoneyState == CancellationStepState.Succeeded && op.FinalizedAtUtc is null,
                _ => op?.FinalizedAtUtc is not null };
            if (matches && Injections++ == 0) throw new IOException("lost paid cancellation acknowledgement");
            return Task.CompletedTask;
        }
    }
    private sealed class FailPaidFinalization : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<Order>().Any(x => x.State == EntityState.Modified && x.Entity.Status == OrderStatus.Cancelled))
                throw new IOException("injected local finalization failure");
            return ValueTask.FromResult(result);
        }
    }
}
