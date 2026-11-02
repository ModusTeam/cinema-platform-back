using Cinema.Domain.Entities;
using FluentAssertions;
using Xunit;
namespace Cinema.OrdersTests;

public class RefundOperationTests
{
    [Fact]
    public void Unknown_is_recoverable_and_success_cannot_dispatch_again()
    {
        DateTime now = DateTime.UtcNow;
        RefundOperation operation = New(now);
        string key = operation.ProviderKey;
        operation.TryClaim(Guid.NewGuid(), now, TimeSpan.FromSeconds(30)).Should().BeTrue();
        operation.Resolve(RefundState.Unknown, now, "timeout");
        operation.State.Should().Be(RefundState.Unknown);
        operation.TryClaim(Guid.NewGuid(), now.AddMinutes(1), TimeSpan.FromSeconds(30)).Should().BeTrue();
        operation.Resolve(RefundState.Succeeded, now.AddMinutes(1), null);
        operation.TryClaim(Guid.NewGuid(), now.AddMinutes(2), TimeSpan.FromSeconds(30)).Should().BeFalse();
        operation.ProviderKey.Should().Be(key);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Zero_or_negative_cash_never_creates_provider_operation(int amount)
    {
        Action create = () => New(DateTime.UtcNow, amount);
        create.Should().Throw<ArgumentException>();
    }
    [Fact]
    public void Resolution_requires_an_owned_dispatch()
    {
        RefundOperation operation = New(DateTime.UtcNow);
        Action resolve = () => operation.Resolve(RefundState.Succeeded, DateTime.UtcNow, null);
        resolve.Should().Throw<InvalidOperationException>();
    }
    private static RefundOperation New(DateTime now, decimal amount = 100m)
        => RefundOperation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "charge", amount, "UAH", now.AddHours(-1), now, Guid.NewGuid(), "test approval", "{}");
}
