using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public interface IPaymentReminderSettingsRepository
{
    Task<RecurringPayment?> FindPaymentWithReminderSettingsAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<bool> TryChangeReminderProfileAsync(Guid userId, Guid paymentId, EffectiveReminderProfile original,
        EffectiveReminderProfile target, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReminderPreviewPayment>> ListExamplesAsync(Guid userId, int offset, int limit, CancellationToken cancellationToken);
}

public sealed record ReminderPreviewPayment(Guid Id, string Name, DateOnly DueDate);

public sealed record EffectiveReminderProfile(ReminderProfile? Profile, bool IsInherited)
{
    public static EffectiveReminderProfile From(RecurringPayment payment) =>
        payment.ReminderSettings is { } settings
            ? new(settings.Profile, false)
            : new(payment.User.GetReminderProfile(), true);
}

public sealed class PaymentReminderSettingsService(IPaymentReminderSettingsRepository payments)
{
    public Task<IReadOnlyList<ReminderPreviewPayment>> ListExamplesAsync(Guid userId, int offset, int limit, CancellationToken cancellationToken) =>
        payments.ListExamplesAsync(userId, offset, limit, cancellationToken);

    public async Task<EffectiveReminderProfile?> GetEffectiveAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await payments.FindPaymentWithReminderSettingsAsync(userId, paymentId, cancellationToken);
        return payment is { IsActive: true } ? EffectiveReminderProfile.From(payment) : null;
    }

    public Task<bool> TryChangeAsync(Guid userId, Guid paymentId, EffectiveReminderProfile original,
        EffectiveReminderProfile target, CancellationToken cancellationToken) =>
        payments.TryChangeReminderProfileAsync(userId, paymentId, original, target, cancellationToken);

    public Task<bool> SetProfileAsync(Guid userId, Guid paymentId, ReminderProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return UpdateAsync(userId, paymentId, payment => payment.SetReminderProfile(profile), cancellationToken);
    }

    public Task<bool> DisableAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken) =>
        UpdateAsync(userId, paymentId, payment => payment.DisableReminders(), cancellationToken);

    public Task<bool> ResetAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken) =>
        UpdateAsync(userId, paymentId, payment => payment.UseUserReminderProfile(), cancellationToken);

    private async Task<bool> UpdateAsync(Guid userId, Guid paymentId, Action<RecurringPayment> update, CancellationToken cancellationToken)
    {
        var payment = await payments.FindPaymentWithReminderSettingsAsync(userId, paymentId, cancellationToken);
        if (payment is null)
            return false;

        update(payment);
        await payments.SaveChangesAsync(cancellationToken);
        return true;
    }
}
