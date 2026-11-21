using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public interface IPaymentReminderSettingsRepository
{
    Task<RecurringPayment?> FindPaymentWithReminderSettingsAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record EffectiveReminderProfile(ReminderProfile? Profile, bool IsInherited);

public sealed class PaymentReminderSettingsService(IPaymentReminderSettingsRepository payments)
{
    public async Task<EffectiveReminderProfile?> GetEffectiveAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await payments.FindPaymentWithReminderSettingsAsync(userId, paymentId, cancellationToken);
        if (payment is null)
            return null;

        return payment.ReminderSettings is { } settings
            ? new EffectiveReminderProfile(settings.Profile, IsInherited: false)
            : new EffectiveReminderProfile(payment.User.GetReminderProfile(), IsInherited: true);
    }

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
