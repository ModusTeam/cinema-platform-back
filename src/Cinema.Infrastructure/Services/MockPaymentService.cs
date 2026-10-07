using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Services;

public class MockPaymentService(ILogger<MockPaymentService> logger) : IPaymentService
{
    public async Task<PaymentResult> ProcessPaymentAsync(decimal amount, string currency, string paymentToken, CancellationToken ct = default)
    {
        logger.LogInformation("Initiating mock payment. Amount: {Amount} {Currency}. Token: {Token}", amount, currency, paymentToken);
        
        await Task.Delay(Random.Shared.Next(500, 1500), ct);
        
        if (paymentToken == "test_fail_token")
        {
            logger.LogWarning("Payment declined for token {Token}", paymentToken);
            return PaymentResult.Failure("Insufficient funds", "card_declined");
        }

        var transactionId = $"txn_{Guid.NewGuid().ToString().Replace("-", "")[..12]}";
        
        logger.LogInformation("Payment successful. Transaction ID: {TransactionId}", transactionId);
        
        return PaymentResult.Success(transactionId);
    }

    public async Task<PaymentResult> RefundPaymentAsync(string transactionId, CancellationToken ct = default)
    {
        logger.LogInformation("Processing refund for Transaction: {TransactionId}", transactionId);
        await Task.Delay(1000, ct);
        
        return PaymentResult.Success($"ref_{Guid.NewGuid().ToString().Replace("-", "")[..10]}");
    }
}