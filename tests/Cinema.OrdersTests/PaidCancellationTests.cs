using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace Cinema.OrdersTests;

public class PaidCancellationTests
{
    [Fact]
    public void Empty_operation_identity_cannot_be_claimed()
    {
        Order order = Paid();
        Action create = () => PaidCancellation.Claim(Guid.Empty, order, order.UserId, false, null, "{}", DateTime.UtcNow);
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Lease_expiry_never_allows_uncertain_money_to_dispatch_again()
    {
        DateTime now = DateTime.UtcNow; Order order = Paid();
        PaidCancellation claim = PaidCancellation.Claim(Guid.NewGuid(), order, order.UserId, false, null, "{}", now);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        claim.TryAcquire(first, now).Should().BeTrue(); claim.BeginMoney(first);
        claim.TryAcquire(second, now.AddSeconds(30)).Should().BeFalse();
        claim.TryAcquire(second, now.AddMinutes(3)).Should().BeTrue();
        claim.MoneyState.Should().Be(CancellationStepState.Unknown);
        Action redispatch = () => claim.BeginMoney(second); redispatch.Should().Throw<InvalidOperationException>();
        // Late actual success remains evidence even after original ownership expired.
        claim.ObserveMoney("late-success"); claim.RefundReference.Should().Be("late-success");
        claim.MoneyState.Should().Be(CancellationStepState.Succeeded);
    }

    [Fact]
    public void Finalization_requires_owned_completed_compensation_and_cannot_repeat()
    {
        DateTime now = DateTime.UtcNow; Order order = Paid();
        PaidCancellation claim = PaidCancellation.Claim(Guid.NewGuid(), order, order.UserId, false, null, "{}", now);
        Guid token = Guid.NewGuid(); claim.TryAcquire(token, now).Should().BeTrue();
        Action incomplete = () => claim.FinalizeCancellation(token, now); incomplete.Should().Throw<InvalidOperationException>();
        Action stolen = () => claim.BeginMoney(Guid.NewGuid()); stolen.Should().Throw<InvalidOperationException>();
        claim.BeginMoney(token); claim.ObserveMoney("success"); claim.FinalizeCancellation(token, now);
        claim.FinalizedAtUtc.Should().Be(now); claim.TryAcquire(Guid.NewGuid(), now.AddDays(1)).Should().BeFalse();
        Action overwrite = () => claim.ObserveMoney("replacement"); overwrite.Should().Throw<InvalidOperationException>();
    }

    private static Order Paid()
    {
        Order order = Order.New(EntityId<Order>.New(), 100, Guid.NewGuid(), EntityId<Session>.New());
        order.MarkAsPaid("charge", "USD"); return order;
    }
}
