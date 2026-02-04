using Cinema.Application.Common.Models.Payments;

namespace Cinema.Application.Common.Interfaces;

public interface IPaymentService
{
    Task<PaymentResult> ProcessPaymentAsync(decimal amount, string currency, string paymentToken, CancellationToken ct = default);
    Task<PaymentResult> RefundPaymentAsync(string transactionId, CancellationToken ct = default);
}