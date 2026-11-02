using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Services;
using Cinema.RefundProviderFixture;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Xunit;
namespace Cinema.OrdersTests;

public class RefundFoundationPostgresTests(PostgresSchemaTemplate template) : IClassFixture<PostgresSchemaTemplate>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_decline_and_newer_success_require_reconciliation_in_either_order(bool successFirst)
    {
        await using Fixture f = await Fixture.Create(template);
        RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        RefundClaim stale = (await store.ClaimAsync(id, Ct))!;
        await f.Due(id, true);
        RefundClaim current = (await store.ClaimAsync(id, Ct))!;
        RefundProviderOutcome decline = new(RefundOutcomeKind.Declined, EvidenceCode: "STALE_DECLINE");
        if (!successFirst) await store.ObserveAsync(stale, decline, Ct);
        await store.ObserveAsync(current, new(RefundOutcomeKind.Succeeded, "newer-success"), Ct);
        (await store.ResolveAsync(current, RefundState.Succeeded, null, Ct)).Should().BeTrue();
        if (successFirst) await store.ObserveAsync(stale, decline, Ct);
        (await store.ResolveAsync(stale, RefundState.Declined, null, Ct)).Should().BeFalse();
        (await store.GetAsync(id, Ct))!.State.Should().Be(RefundState.ReconciliationRequired);
        (await store.GetAsync(id, Ct))!.LastCode.Should().Be("CONFLICTING_PROVIDER_OUTCOMES");
        await using ApplicationDbContext verify = f.Context();
        (await verify.Set<RefundProviderObservation>().CountAsync(Ct)).Should().Be(2);
        UnsupportedProvider provider = new();
        (await new RefundExecutor(f.Store(), provider).ExecuteAsync(id, Ct)).Should().Be(RefundState.ReconciliationRequired);
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Recovery_cannot_promote_success_when_both_outcomes_were_committed_before_head_resolution()
    {
        await using Fixture f = await Fixture.Create(template);
        RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        RefundClaim stale = (await store.ClaimAsync(id, Ct))!;
        await f.Due(id, true);
        RefundClaim current = (await store.ClaimAsync(id, Ct))!;
        await store.ObserveAsync(stale, new(RefundOutcomeKind.Declined), Ct);
        await store.ObserveAsync(current, new(RefundOutcomeKind.Succeeded, "unresolved-success"), Ct);
        await f.Due(id, true);
        UnsupportedProvider provider = new();
        (await new RefundExecutor(f.Store(), provider).ExecuteAsync(id, Ct)).Should().Be(RefundState.ReconciliationRequired);
        provider.Calls.Should().Be(0);
        (await store.GetAsync(id, Ct))!.LastCode.Should().Be("CONFLICTING_PROVIDER_OUTCOMES");
        (await store.SuccessfulObservationAsync(id, Ct))!.ProviderReference.Should().Be("unresolved-success");
    }

    [Fact]
    public async Task Repeated_observation_with_submicrosecond_timestamp_is_an_exact_replay()
    {
        await using Fixture f = await Fixture.Create(template);
        RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        RefundClaim claim = (await store.ClaimAsync(id, Ct))!;
        DateTime occurred = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        RefundProviderOutcome outcome = new(RefundOutcomeKind.Succeeded, "microsecond-reference", "CONFIRMED", occurred);
        await store.ObserveAsync(claim, outcome, Ct);
        await f.Store().ObserveAsync(claim, outcome, Ct);
        await using ApplicationDbContext verify = f.Context();
        RefundProviderObservation receipt = await verify.Set<RefundProviderObservation>().SingleAsync(Ct);
        receipt.ProviderOccurredAtUtc.Should().Be(occurred.AddTicks(-7));
        receipt.ClaimToken.Should().Be(claim.Token);
        Func<Task> conflict = () => store.ObserveAsync(claim, outcome with { ProviderOccurredAtUtc = occurred.AddTicks(10) }, Ct);
        await conflict.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_success_and_newer_decline_require_reconciliation_in_either_commit_order(bool successFirst)
    {
        await using Fixture f = await Fixture.Create(template);
        RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        RefundClaim stale = (await store.ClaimAsync(id, Ct))!;
        await f.Due(id, true);
        RefundClaim current = (await store.ClaimAsync(id, Ct))!;
        RefundProviderOutcome success = new(RefundOutcomeKind.Succeeded, "authoritative-late-success");
        await store.ObserveAsync(current, new(RefundOutcomeKind.Declined, EvidenceCode: "DECLINED"), Ct);
        PauseSave pause = new();
        Task first = successFirst
            ? f.Store(pause).ObserveAsync(stale, success, Ct)
            : f.Store(pause).ResolveAsync(current, RefundState.Declined, "DECLINED", Ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Task second = successFirst
            ? store.ResolveAsync(current, RefundState.Declined, "DECLINED", Ct)
            : store.ObserveAsync(stale, success, Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using ApplicationDbContext monitor = f.Context();
            while (!await monitor.Database.SqlQuery<bool>($"""SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock') AS "Value" """).SingleAsync(timeout.Token)) await Task.Delay(10, timeout.Token);
            second.IsCompleted.Should().BeFalse();
        }
        finally { pause.Release.TrySetResult(); }
        await first; await second;
        (await store.ResolveAsync(stale, RefundState.Succeeded, null, Ct)).Should().BeFalse();
        RefundOperation recovered = (await f.Store().GetAsync(id, Ct))!;
        recovered.State.Should().Be(RefundState.ReconciliationRequired);
        recovered.LastCode.Should().Be("CONFLICTING_PROVIDER_OUTCOMES");
        await using ApplicationDbContext verify = f.Context();
        (await verify.Set<RefundProviderObservation>().CountAsync(Ct)).Should().Be(2);
        (await verify.Set<RefundProviderObservation>().SingleAsync(x => x.ClaimToken == current.Token, Ct)).Outcome.Should().Be(RefundState.Declined);
        (await store.SuccessfulObservationAsync(id, Ct))!.ProviderReference.Should().Be("authoritative-late-success");
        await f.Store().ObserveAsync(stale, success, Ct);
        UnsupportedProvider provider = new();
        (await new RefundExecutor(f.Store(), provider).ExecuteAsync(id, Ct)).Should().Be(RefundState.ReconciliationRequired);
        provider.Calls.Should().Be(0);
        (await verify.Orders.SingleAsync(Ct)).Status.Should().Be(Cinema.Domain.Enums.OrderStatus.Paid);
    }

    [Fact]
    public async Task Concurrent_creation_waits_on_real_order_lock_and_returns_one_identity()
    {
        await using Fixture f = await Fixture.Create(template);
        PauseSave pause = new();
        RefundOperationStore first = f.Store(pause), second = f.Store();
        Task<Cinema.Domain.Shared.Result<RefundOperation>> a = first.PersistApprovedAsync(f.Candidate(), Ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Task<Cinema.Domain.Shared.Result<RefundOperation>> b = second.PersistApprovedAsync(f.Candidate(), Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using ApplicationDbContext monitor = f.Context();
            while (!await monitor.Database.SqlQuery<bool>($"""SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock') AS "Value" """).SingleAsync(timeout.Token)) await Task.Delay(10, timeout.Token);
            b.IsCompleted.Should().BeFalse();
        }
        finally { pause.Release.TrySetResult(); }
        (await a).IsSuccess.Should().BeTrue(); (await b).IsSuccess.Should().BeTrue();
        (await a).Value.Id.Should().Be((await b).Value.Id);
        await using ApplicationDbContext verify = f.Context();
        (await verify.Set<RefundOperation>().CountAsync(Ct)).Should().Be(1);
    }
    [Fact]
    public async Task Approval_save_failure_rolls_back_and_does_not_change_order()
    {
        await using Fixture f = await Fixture.Create(template);
        Func<Task> create = () => f.Store(new FailSave()).PersistApprovedAsync(f.Candidate(), Ct);
        await create.Should().ThrowAsync<IOException>();
        await using ApplicationDbContext verify = f.Context();
        (await verify.Set<RefundOperation>().CountAsync(Ct)).Should().Be(0);
        (await verify.Orders.SingleAsync(Ct)).Status.Should().Be(Cinema.Domain.Enums.OrderStatus.Paid);
    }
    [Fact]
    public async Task Stale_worker_cannot_resolve_new_claim_but_late_success_evidence_is_retained()
    {
        await using Fixture f = await Fixture.Create(template); RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        RefundClaim old = (await store.ClaimAsync(id, Ct))!;
        (await store.ClaimAsync(id, Ct)).Should().BeNull();
        await f.Due(id, true);
        RefundClaim current = (await store.ClaimAsync(id, Ct))!; current.Token.Should().NotBe(old.Token); current.FirstDispatch.Should().BeFalse();
        await store.ObserveAsync(old, new(RefundOutcomeKind.Succeeded, "late-reference"), Ct);
        (await store.ResolveAsync(old, RefundState.Succeeded, null, Ct)).Should().BeFalse();
        (await store.ResolveAsync(old, RefundState.Declined, null, Ct)).Should().BeFalse();
        (await store.GetAsync(id, Ct))!.LeaseToken.Should().Be(current.Token);
        (await store.SuccessfulObservationAsync(id, Ct))!.ProviderReference.Should().Be("late-reference");
        (await store.ResolveAsync(current, RefundState.Succeeded, null, Ct)).Should().BeTrue();
        (await store.ClaimAsync(id, Ct)).Should().BeNull();
    }
    [Fact]
    public async Task Lost_provider_response_recovers_by_lookup_in_fresh_executor_without_second_money_call()
    {
        await using Fixture f = await Fixture.Create(template); RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        DurableRefundProvider lost = new(f.Connection, loseResponse: true);
        (await new RefundExecutor(store, lost).ExecuteAsync(id, Ct)).Should().Be(RefundState.Unknown);
        await f.Due(id);
        DurableRefundProvider restarted = new(f.Connection);
        (await new RefundExecutor(f.Store(), restarted).ExecuteAsync(id, Ct)).Should().Be(RefundState.Succeeded);
        restarted.Submits.Should().Be(0); restarted.Lookups.Should().Be(1);
        await using ApplicationDbContext verify = f.Context();
        (await verify.Set<RefundProviderObservation>().CountAsync(Ct)).Should().Be(2);
        (await verify.Orders.SingleAsync(Ct)).Status.Should().Be(Cinema.Domain.Enums.OrderStatus.Paid);
    }
    [Theory]
    [InlineData(RefundOutcomeKind.Declined, RefundState.Declined)]
    [InlineData(RefundOutcomeKind.Pending, RefundState.Pending)]
    [InlineData(RefundOutcomeKind.Unknown, RefundState.Unknown)]
    public async Task Provider_outcomes_are_not_conflated(RefundOutcomeKind outcome, RefundState expected)
    {
        await using Fixture f = await Fixture.Create(template); RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        DurableRefundProvider provider = new(f.Connection, outcome);
        (await new RefundExecutor(store, provider).ExecuteAsync(id, Ct)).Should().Be(expected);
        if (expected == RefundState.Declined) { await new RefundExecutor(store, provider).ExecuteAsync(id, Ct); provider.Submits.Should().Be(1); }
    }
    [Fact]
    public async Task Expired_dispatch_without_safe_capabilities_does_not_submit_again()
    {
        await using Fixture f = await Fixture.Create(template); RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        await store.ClaimAsync(id, Ct); await f.Due(id, true);
        UnsupportedProvider provider = new();
        (await new RefundExecutor(store, provider).ExecuteAsync(id, Ct)).Should().Be(RefundState.ReconciliationRequired);
        provider.Calls.Should().Be(0);
    }
    [Fact]
    public async Task Ambiguous_commit_does_not_replay_insert_and_fresh_scope_finds_original_identity()
    {
        await using Fixture f = await Fixture.Create(template); RefundOperation candidate = f.Candidate();
        LostCommit fault = new();
        Func<Task> insert = () => f.Store(fault).PersistApprovedAsync(candidate, Ct);
        await insert.Should().ThrowAsync<IOException>(); fault.Count.Should().Be(1);
        (await f.Store().GetAsync(candidate.Id, Ct))!.ProviderKey.Should().Be(candidate.ProviderKey);
        (await f.Store().PersistApprovedAsync(f.Candidate(), Ct)).Value.Id.Should().Be(candidate.Id);
    }
    [Fact]
    public async Task Durable_mock_survives_actual_process_exit_after_commit_and_rejects_conflicting_payload()
    {
        await using Fixture f = await Fixture.Create(template); RefundOperation candidate = f.Candidate();
        RefundProviderRequest request = new(candidate.Id, candidate.ProviderKey, candidate.ChargeReference, candidate.Amount, candidate.Currency);
        string root = Directory.GetCurrentDirectory(); while (!File.Exists(Path.Combine(root, "Cinema.sln"))) root = Directory.GetParent(root)!.FullName;
        DirectoryInfo output = new(AppContext.BaseDirectory);
        string configuration = output.Parent!.Name;
        string host = Path.Combine(root, "tests", "RefundProviderFixture", "bin", configuration, output.Name, "RefundProviderFixture.dll");
        File.Exists(host).Should().BeTrue("the fixture must be built in the active test configuration");
        async Task<(int Code, string Output)> Run(string mode)
        {
            ProcessStartInfo info = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(host); info.ArgumentList.Add(mode);
            info.Environment["REFUND_FIXTURE_CONNECTION"] = f.Connection; info.Environment["REFUND_FIXTURE_REQUEST"] = JsonSerializer.Serialize(request);
            using Process child = Process.Start(info)!;
            Task<string> output = child.StandardOutput.ReadToEndAsync(Ct); Task<string> errors = child.StandardError.ReadToEndAsync(Ct);
            await child.WaitForExitAsync(Ct); string stderr = await errors; stderr.Should().BeEmpty(); return (child.ExitCode, await output);
        }
        (await Run("submit-lost")).Code.Should().Be(93);
        var lookup = await Run("lookup"); lookup.Code.Should().Be(0);
        JsonSerializer.Deserialize<RefundLookup>(lookup.Output)!.Outcome!.Kind.Should().Be(RefundOutcomeKind.Succeeded);
        DurableRefundProvider provider = new(f.Connection);
        RefundProviderOutcome original = await provider.SubmitAsync(request, Ct);
        (await provider.SubmitAsync(request, Ct)).Should().Be(original);
        foreach (RefundProviderRequest conflict in new[] { request with { Amount = 101 }, request with { ChargeReference = "other" }, request with { IdempotencyKey = "other" } })
        {
            Func<Task> submit = () => provider.SubmitAsync(conflict, Ct); await submit.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task Late_success_after_timeout_and_ownership_loss_is_still_durable_evidence()
    {
        await using Fixture f = await Fixture.Create(template); RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        LateProvider provider = new(); Task<RefundState?> running = new RefundExecutor(store, provider).ExecuteAsync(id, Ct);
        await provider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await f.Due(id, true);
        RefundClaim next = (await store.ClaimAsync(id, Ct))!;
        await store.ResolveAsync(next, RefundState.ReconciliationRequired, "MANUAL_LOOKUP", Ct);
        provider.Release.TrySetResult(new(RefundOutcomeKind.Succeeded, "late-after-timeout"));
        await running;
        (await store.SuccessfulObservationAsync(id, Ct))!.ProviderReference.Should().Be("late-after-timeout");
        (await store.GetAsync(id, Ct))!.State.Should().Be(RefundState.ReconciliationRequired);
    }
    private sealed class LateProvider : IRefundProvider
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RefundProviderOutcome> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsStableIdempotency => false; public bool SupportsAuthoritativeLookup => false;
        public async Task<RefundProviderOutcome> SubmitAsync(RefundProviderRequest request, CancellationToken ct)
        { Calls++; Entered.TrySetResult(); using var registration = ct.Register(() => CancellationObserved.TrySetResult()); return await Release.Task; }
        public Task<RefundLookup> LookupAsync(RefundProviderRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Last_crashed_attempt_exhausts_budget_but_retained_success_needs_no_money_retry()
    {
        await using Fixture f = await Fixture.Create(template); RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        RefundClaim? last = null;
        for (int i = 0; i < 8; i++) { last = await store.ClaimAsync(id, Ct); last.Should().NotBeNull(); await f.Due(id, true); }
        (await store.ClaimAsync(id, Ct)).Should().BeNull();
        (await store.GetAsync(id, Ct))!.State.Should().Be(RefundState.ReconciliationRequired);
        await store.ObserveAsync(last!, new(RefundOutcomeKind.Succeeded, "final-success"), Ct);
        UnsupportedProvider provider = new();
        (await new RefundExecutor(store, provider).ExecuteAsync(id, Ct)).Should().Be(RefundState.Succeeded);
        provider.Calls.Should().Be(0);
    }
    [Fact]
    public async Task Identity_constraints_and_immutable_EF_evidence_are_enforced()
    {
        await using Fixture f = await Fixture.Create(template); RefundOperationStore store = f.Store();
        RefundOperation op = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value;
        await using ApplicationDbContext db = f.Context();
        RefundOperation tracked = await db.Set<RefundOperation>().SingleAsync(Ct);
        db.Entry(tracked).Property(x => x.Amount).CurrentValue = 200;
        Func<Task> update = () => db.SaveChangesAsync(Ct); await update.Should().ThrowAsync<InvalidOperationException>(); db.ChangeTracker.Clear();
        Guid copy = Guid.NewGuid(); string copyKey = "refund:" + copy.ToString("N");
        Func<Task> duplicate = () => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO refund_operations (id,order_id,user_id,session_id,charge_reference,amount,currency,paid_at_utc,approved_at_utc,actor_id,reason,admission_evidence,provider_key,state,lease_token,lease_expires_at_utc,dispatch_started_at_utc,next_attempt_at_utc,attempts,last_code) SELECT {copy},order_id,user_id,session_id,charge_reference,amount,currency,paid_at_utc,approved_at_utc,actor_id,reason,admission_evidence,{copyKey},state,lease_token,lease_expires_at_utc,dispatch_started_at_utc,next_attempt_at_utc,attempts,last_code FROM refund_operations", Ct);
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        (await store.PersistApprovedAsync(RefundOperation.Create(Guid.NewGuid(), op.OrderId.Value, op.UserId, op.SessionId, op.ChargeReference, 100, "USD", op.PaidAtUtc, op.ApprovedAtUtc, op.ActorId, op.Reason, op.AdmissionEvidence), Ct)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Caller_cancellation_does_not_discard_returned_success()
    {
        await using Fixture f = await Fixture.Create(template); RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        LateProvider provider = new(); using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<RefundState?> running = new RefundExecutor(store, provider).ExecuteAsync(id, caller.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); caller.Cancel();
        provider.Release.TrySetResult(new(RefundOutcomeKind.Succeeded, "after-caller-cancel"));
        (await running).Should().Be(RefundState.Succeeded);
        (await store.SuccessfulObservationAsync(id, Ct))!.ProviderReference.Should().Be("after-caller-cancel");
    }

    [Fact]
    public async Task Concurrent_executors_cannot_start_two_provider_calls_and_hold_no_row_lock_during_call()
    {
        await using Fixture f = await Fixture.Create(template); RefundOperationStore store = f.Store();
        Guid id = (await store.PersistApprovedAsync(f.Candidate(), Ct)).Value.Id;
        LateProvider provider = new(); Task<RefundState?> first = new RefundExecutor(store, provider).ExecuteAsync(id, Ct);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await new RefundExecutor(f.Store(), provider).ExecuteAsync(id, Ct)).Should().Be(RefundState.Dispatching);
        provider.Calls.Should().Be(1);
        provider.Release.TrySetResult(new(RefundOutcomeKind.Succeeded, "concurrent-success"));
        (await first).Should().Be(RefundState.Succeeded);
    }

    [Fact]
    public async Task Crash_after_claim_before_network_uses_authoritative_lookup_then_original_key()
    {
        await using Fixture f=await Fixture.Create(template);RefundOperationStore store=f.Store();
        Guid id=(await store.PersistApprovedAsync(f.Candidate(),Ct)).Value.Id;
        RefundClaim original=(await store.ClaimAsync(id,Ct))!;await f.Due(id,true);
        DurableRefundProvider provider=new(f.Connection);
        (await new RefundExecutor(f.Store(),provider).ExecuteAsync(id,Ct)).Should().Be(RefundState.Succeeded);
        provider.Submits.Should().Be(1);
        (await store.GetAsync(id,Ct))!.ProviderKey.Should().Be(original.Request.IdempotencyKey);
    }
    [Fact]
    public async Task Failed_head_confirmation_cannot_erase_committed_success_observation()
    {
        await using Fixture f=await Fixture.Create(template);
        Guid id=(await f.Store().PersistApprovedAsync(f.Candidate(),Ct)).Value.Id;
        DurableRefundProvider provider=new(f.Connection);
        Func<Task> execute=()=>new RefundExecutor(f.Store(new FailResolutionSave()),provider).ExecuteAsync(id,Ct);
        await execute.Should().ThrowAsync<IOException>();
        (await f.Store().SuccessfulObservationAsync(id,Ct))!.Kind.Should().Be(RefundOutcomeKind.Succeeded);
        await f.Due(id,true);
        UnsupportedProvider noNetwork=new();
        (await new RefundExecutor(f.Store(),noNetwork).ExecuteAsync(id,Ct)).Should().Be(RefundState.Succeeded);
        noNetwork.Calls.Should().Be(0);provider.Submits.Should().Be(1);
    }
    private sealed class FailResolutionSave : SaveChangesInterceptor
    {
        private int calls;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken cancellationToken=default)
        {if(++calls==3)throw new IOException("Injected head confirmation failure");return ValueTask.FromResult(result);}
    }

    private sealed class Fixture(string connection, string admin, string name, Order order) : IAsyncDisposable
    {
        public string Connection => connection;
        private readonly DateTime approved = DateTime.UtcNow;
        public ApplicationDbContext Context(params IInterceptor[] interceptors) => new(Options(interceptors));
        private DbContextOptions<ApplicationDbContext> Options(IInterceptor[] interceptors) => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connection, pg => { pg.UseVector(); pg.EnableRetryOnFailure(3); }).UseSnakeCaseNamingConvention().AddInterceptors(interceptors).Options;
        public RefundOperationStore Store(params IInterceptor[] interceptors) => new(new PooledDbContextFactory<ApplicationDbContext>(Options(interceptors)));
        public RefundOperation Candidate() => RefundOperation.Create(Guid.NewGuid(), order.Id.Value, order.UserId, order.SessionId.Value, order.PaymentTransactionId!, 100m, "UAH", order.PaidAt!.Value, approved, order.UserId, "fixture approval", "{}");
        public async Task Due(Guid id, bool lease = false)
        {
            await using ApplicationDbContext db = Context();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE refund_operations SET next_attempt_at_utc=clock_timestamp()-interval '1 second' WHERE id={id}", Ct);
            if (lease) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE refund_operations SET lease_expires_at_utc=clock_timestamp()-interval '1 second' WHERE id={id}", Ct);
        }
        public static async Task<Fixture> Create(PostgresSchemaTemplate template)
        {
            var (connection, admin, name) = await template.CreateDatabaseAsync();
            DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection, pg => pg.UseVector()).UseSnakeCaseNamingConvention().Options;
            await using ApplicationDbContext db = new(options);
            User user = new() { Id = Guid.NewGuid(), UserName = "refund-fixture" };
            Hall hall = Hall.Create(EntityId<Hall>.New(), "refund"); Movie movie = Movie.CreateManual("refund", "test", 90, 2026, MovieStatus.ComingSoon);
            Pricing pricing = Pricing.New(EntityId<Pricing>.New(), "refund"); DateTime start = DateTime.UtcNow.AddDays(3);
            Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2), movie.Id, hall.Id, pricing.Id);
            Order order = Order.New(EntityId<Order>.New(), 100m, user.Id, session.Id); order.MarkAsPaid("charge_" + Guid.NewGuid().ToString("N"));
            db.AddRange(user, hall, movie, pricing, session, order); await db.SaveChangesAsync(Ct);
            await DurableRefundProvider.InitializeAsync(connection, Ct);
            return new(connection, admin, name, order);
        }
        public async ValueTask DisposeAsync() { await using NpgsqlConnection db = new(admin); await db.OpenAsync(); await using NpgsqlCommand drop = new($"DROP DATABASE {name} WITH (FORCE)", db); await drop.ExecuteNonQueryAsync(); }
    }
    private sealed class PauseSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); return result; }
    }
    private sealed class FailSave : SaveChangesInterceptor
    { public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw new IOException("Injected approval failure"); }
    private sealed class LostCommit : DbTransactionInterceptor
    {
        public int Count { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken cancellationToken = default)
        { Count++; throw new IOException("Lost commit response"); }
    }
    private sealed class UnsupportedProvider : IRefundProvider
    {
        public int Calls { get; private set; }
        public bool SupportsStableIdempotency => false; public bool SupportsAuthoritativeLookup => false;
        public Task<RefundProviderOutcome> SubmitAsync(RefundProviderRequest request, CancellationToken ct) { Calls++; throw new InvalidOperationException("Must not dispatch"); }
        public Task<RefundLookup> LookupAsync(RefundProviderRequest request, CancellationToken ct) => throw new InvalidOperationException("No lookup");
    }
}
