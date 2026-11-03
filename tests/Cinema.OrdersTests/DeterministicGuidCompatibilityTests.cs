using Cinema.Application.Common.Utils;
using FluentAssertions;
using Xunit;

namespace Cinema.OrdersTests;

public class DeterministicGuidCompatibilityTests
{
    // These are persisted Loyalty idempotency identities, not cryptographic tokens.
    [Theory]
    [InlineData("deduct-00000000-0000-0000-0000-000000000001", "89fb51a3-fc6d-345e-8fcc-d13e873b07a1")]
    [InlineData("refund-00000000-0000-0000-0000-000000000001", "4985b961-71e0-3bf8-beb4-bff0eda24567")]
    [InlineData("deduct-63f4b838-c734-4486-9794-7735d040e32b", "5c6c8114-2a06-313f-a211-34a7d428fddb")]
    [InlineData("refund-63f4b838-c734-4486-9794-7735d040e32b", "08097201-e078-3784-9b49-12fd4b6614f3")]
    public void Existing_operation_identities_remain_unchanged(string input, string expected)
    {
        DeterministicGuid.Create(input).Should().Be(Guid.Parse(expected));
        DeterministicGuid.Create(input).Should().Be(DeterministicGuid.Create(input));
    }
}
