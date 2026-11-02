using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace Cinema.OrdersTests;

public class RefundAdmissionRulesTests
{
    [Theory]
    [InlineData(-10, false)]
    [InlineData(0, false)]
    [InlineData(10, true)]
    public void Ordinary_cutoff_is_strictly_more_than_sixty_minutes(long offsetTicks, bool eligible)
    {
        DateTime approval = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        RefundOperation.IsOrdinaryApprovalAllowed(approval.AddHours(1).AddTicks(offsetTicks), approval).Should().Be(eligible);
    }

    [Fact]
    public void Confirmed_currency_and_time_are_immutable_including_replay()
    {
        Order order = New();
        order.MarkAsPaid("charge", "USD");
        DateTime? paid = order.PaidAt;
        order.MarkAsPaid("charge", "USD");
        order.MarkAsPaid("charge");
        order.PaidCurrency.Should().Be("USD");
        order.PaidAt.Should().Be(paid);
        Action conflict = () => order.MarkAsPaid("charge", "UAH");
        conflict.Should().Throw<DomainException>();
        order.PaidCurrency.Should().Be("USD");
    }

    [Fact]
    public void Legacy_unknown_currency_is_never_filled_by_paid_replay()
    {
        Order order = New(); order.MarkAsPaid("legacy");
        Action guess = () => order.MarkAsPaid("legacy", "UAH");
        guess.Should().Throw<DomainException>();
        order.PaidCurrency.Should().BeNull();
    }

    [Fact]
    public void Zero_cash_does_not_claim_a_monetary_currency()
    {
        Order order = New(); order.ApplyLoyaltyDiscount(100); order.MarkAsPaid("zero", "UAH");
        order.PaidCurrency.Should().BeNull(); order.PaidAmount.Should().Be(0);
    }

    [Theory]
    [InlineData("uah")]
    [InlineData("US")]
    [InlineData("USDX")]
    [InlineData("")]
    public void Currency_must_be_an_explicit_uppercase_code(string currency)
    {
        Action invalid = () => New().MarkAsPaid("charge", currency);
        invalid.Should().Throw<DomainException>();
    }

    private static Order New() => Order.New(EntityId<Order>.New(), 100, Guid.NewGuid(), EntityId<Session>.New());
}
