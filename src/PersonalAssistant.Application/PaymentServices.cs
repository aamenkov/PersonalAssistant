using System.Globalization;
using System.Text.Json;
using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public interface IPaymentRepository
{
    Task AddAsync(RecurringPayment payment, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecurringPayment>> GetActiveAsync(Guid userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IConversationStateRepository
{
    Task<ConversationState?> FindAsync(Guid userId, CancellationToken cancellationToken);
    Task AddAsync(ConversationState state, CancellationToken cancellationToken);
    void Remove(ConversationState state);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record PaymentListItem(string Name, decimal Amount, string Currency, DateOnly DueDate, RecurrenceUnit RecurrenceUnit);

public sealed class PaymentService(IPaymentRepository payments)
{
    public async Task CreateAsync(
        Guid userId,
        string name,
        decimal amount,
        string currency,
        int recurrenceInterval,
        RecurrenceUnit recurrenceUnit,
        DateOnly nextPaymentDate,
        CancellationToken cancellationToken)
    {
        var payment = RecurringPayment.Create(userId, name, amount, currency, recurrenceInterval, recurrenceUnit, nextPaymentDate, DateTime.UtcNow);
        await payments.AddAsync(payment, cancellationToken);
        await payments.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentListItem>> GetActiveAsync(Guid userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var entities = await payments.GetActiveAsync(userId, from, to, cancellationToken);
        return entities
            .Where(x => x.NextPaymentDate.HasValue)
            .OrderBy(x => x.NextPaymentDate)
            .Select(x => new PaymentListItem(x.Name, x.Amount, x.Currency, x.NextPaymentDate!.Value, x.RecurrenceUnit))
            .ToList();
    }
}

public sealed class PaymentConversationService(
    IConversationStateRepository states,
    PaymentService payments)
{
    public async Task<string> BeginAsync(Guid userId, CancellationToken cancellationToken)
    {
        var existing = await states.FindAsync(userId, cancellationToken);
        var payload = JsonSerializer.Serialize(new PaymentDraftState());
        if (existing is not null)
            existing.UpdatePayload(payload, DateTime.UtcNow);
        else
            await states.AddAsync(ConversationState.Create(userId, ConversationKind.AddPayment, payload, DateTime.UtcNow), cancellationToken);
        await states.SaveChangesAsync(cancellationToken);
        return "Введите название платежа:";
    }

    public async Task<string?> HandleInputAsync(Guid userId, string input, CancellationToken cancellationToken)
    {
        var state = await states.FindAsync(userId, cancellationToken);
        if (state is null || state.Kind != ConversationKind.AddPayment)
            return null;

        var draft = JsonSerializer.Deserialize<PaymentDraftState>(state.PayloadJson) ?? new PaymentDraftState();
        input = input.Trim();
        if (input.Length == 0)
            return "Значение не может быть пустым. Попробуйте еще раз:";

        switch (draft.Step)
        {
            case PaymentDraftStep.Name:
                draft.Name = input;
                draft.Step = PaymentDraftStep.Amount;
                break;
            case PaymentDraftStep.Amount:
                if (!decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
                    return "Введите положительную сумму, например `30.50`:";
                draft.Amount = amount;
                draft.Step = PaymentDraftStep.Currency;
                break;
            case PaymentDraftStep.Currency:
                if (input.Length != 3 || input.Any(x => !char.IsLetter(x)))
                    return "Введите трехбуквенный код валюты, например `RUB` или `USD`:";
                draft.Currency = input.ToUpperInvariant();
                draft.Step = PaymentDraftStep.Recurrence;
                break;
            case PaymentDraftStep.Recurrence:
                if (!TryParseRecurrence(input, out var recurrenceUnit))
                    return "Введите периодичность: `weekly`, `monthly`, `yearly` или `once` (можно по-русски).";
                draft.RecurrenceUnit = recurrenceUnit;
                draft.Step = PaymentDraftStep.NextPaymentDate;
                break;
            case PaymentDraftStep.NextPaymentDate:
                if (!DateOnly.TryParseExact(input, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var nextDate))
                    return "Введите дату в формате `ГГГГ-ММ-ДД`, например `2026-08-15`:";
                draft.NextPaymentDate = nextDate;
                draft.Step = PaymentDraftStep.Confirmation;
                break;
            case PaymentDraftStep.Confirmation:
                if (input.Equals("нет", StringComparison.OrdinalIgnoreCase) || input.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    states.Remove(state);
                    await states.SaveChangesAsync(cancellationToken);
                    return "Создание платежа отменено.";
                }
                if (!input.Equals("да", StringComparison.OrdinalIgnoreCase) && !input.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    return "Введите `да` для сохранения или `нет` для отмены:";

                await payments.CreateAsync(userId, draft.Name!, draft.Amount!.Value, draft.Currency!, 1, draft.RecurrenceUnit!.Value, draft.NextPaymentDate!.Value, cancellationToken);
                states.Remove(state);
                await states.SaveChangesAsync(cancellationToken);
                return "Платеж сохранен. Используйте /payments для просмотра.";
        }

        state.UpdatePayload(JsonSerializer.Serialize(draft), DateTime.UtcNow);
        await states.SaveChangesAsync(cancellationToken);
        return draft.Step switch
        {
            PaymentDraftStep.Amount => "Введите сумму:",
            PaymentDraftStep.Currency => "Введите валюту (например, RUB):",
            PaymentDraftStep.Recurrence => "Введите периодичность: weekly, monthly, yearly или once:",
            PaymentDraftStep.NextPaymentDate => "Введите дату следующего платежа в формате ГГГГ-ММ-ДД:",
            PaymentDraftStep.Confirmation => $"Проверьте платеж:\n{draft.Name} — {draft.Amount:0.##} {draft.Currency}\nДата: {draft.NextPaymentDate:yyyy-MM-dd}\nПериодичность: {draft.RecurrenceUnit}\n\nСохранить? Введите да или нет.",
            _ => "Введите название платежа:"
        };
    }

    private static bool TryParseRecurrence(string input, out RecurrenceUnit unit)
    {
        unit = input.ToLowerInvariant() switch
        {
            "weekly" or "еженедельно" or "неделя" => RecurrenceUnit.Week,
            "monthly" or "ежемесячно" or "месяц" => RecurrenceUnit.Month,
            "yearly" or "annually" or "ежегодно" or "год" => RecurrenceUnit.Year,
            "once" or "однократно" or "один раз" => RecurrenceUnit.Once,
            _ => (RecurrenceUnit)(-1)
        };
        return unit >= RecurrenceUnit.Once;
    }

    private sealed class PaymentDraftState
    {
        public PaymentDraftStep Step { get; set; }
        public string? Name { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
        public RecurrenceUnit? RecurrenceUnit { get; set; }
        public DateOnly? NextPaymentDate { get; set; }
    }

    private enum PaymentDraftStep
    {
        Name = 0,
        Amount = 1,
        Currency = 2,
        Recurrence = 3,
        NextPaymentDate = 4,
        Confirmation = 5
    }
}
