namespace Cinema.Application.Common.Models.Payments;

public class PaymentResult
{
    public bool IsSuccess { get; }
    public string? TransactionId { get; }
    public string? ErrorMessage { get; }
    public string? FailureCode { get; }

    private PaymentResult(bool isSuccess, string? transactionId, string? errorMessage, string? failureCode)
    {
        IsSuccess = isSuccess;
        TransactionId = transactionId;
        ErrorMessage = errorMessage;
        FailureCode = failureCode;
    }

    public static PaymentResult Success(string transactionId) 
        => new(true, transactionId, null, null);

    public static PaymentResult Failure(string errorMessage, string failureCode = "payment_failed") 
        => new(false, null, errorMessage, failureCode);
}