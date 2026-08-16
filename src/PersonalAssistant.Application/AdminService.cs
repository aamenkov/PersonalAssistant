namespace PersonalAssistant.Application;

public sealed class AdminService(IPaymentRepository payments)
{
    public Task<int> ClearPaymentHistoryAsync(Guid userId, CancellationToken cancellationToken) =>
        payments.ClearTransactionHistoryAsync(userId, cancellationToken);

    public Task<int> DeleteAllPaymentsAsync(Guid userId, CancellationToken cancellationToken) =>
        payments.DeleteAllPaymentsAsync(userId, cancellationToken);
}
