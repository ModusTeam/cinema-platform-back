using Cinema.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cinema.OrdersTests;

public class MockPaymentServiceTests
{
    [Theory]
    [InlineData("test_fail_token")]
    [InlineData("sensitive-card-token")]
    public async Task Payment_Logs_Do_Not_Contain_Token(string token)
    {
        CapturingLogger logger = new();
        MockPaymentService payment = new(logger);

        await payment.ProcessPaymentAsync(25m, "UAH", token, TestContext.Current.CancellationToken);

        logger.Messages.Should().NotContain(message => message.Contains(token, StringComparison.Ordinal));
        logger.Messages.Should().Contain(message => message.Contains("25", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : ILogger<MockPaymentService>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
