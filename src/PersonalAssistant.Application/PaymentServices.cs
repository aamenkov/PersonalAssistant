using System.Globalization;
using System.Text.Json;
using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public interface IPaymentRepository
{
    Task AddAsync(RecurringPayment payment, CancellationToken cancellationToken);
    Task<RecurringPayment?> FindForOwnerAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecurringPayment>> GetActiveAsync(Guid userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentTransaction>> GetTransactionsForOwnerAsync(Guid userId, Guid? paymentId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IConversationStateRepository
{
    Task<ConversationState?> FindAsync(Guid userId, CancellationToken cancellationToken);
    Task AddAsync(ConversationState state, CancellationToken cancellationToken);
    void Remove(ConversationState state);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public static class UserInputParser
{
    public static bool TryParsePositiveAmount(string input, out decimal amount)
    {
        amount = 0;
        var normalized = input.Trim();
        if (normalized.Contains('.') && normalized.Contains(','))
            return false;

        normalized = normalized.Replace(',', '.');
        return decimal.TryParse(
            normalized,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out amount)
            && amount > 0;
    }

    public static bool TryParseYearMonth(string input, out int year, out int month)
    {
        if (DateTime.TryParseExact(input.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var selected))
        {
            year = selected.Year;
            month = selected.Month;
            return true;
        }

        year = 0;
        month = 0;
        return false;
    }
}

public sealed record PaymentListItem(Guid Id, string Name, decimal Amount, string Currency, DateOnly DueDate, RecurrenceUnit RecurrenceUnit, PaymentMethod PaymentMethod, bool IsAutoDebit);

public sealed record PaymentDetails(
    Guid Id,
    string Name,
    decimal Amount,
    string Currency,
    int RecurrenceInterval,
    RecurrenceUnit RecurrenceUnit,
    DateOnly NextPaymentDate,
    PaymentMethod PaymentMethod,
    bool IsAutoDebit,
    string? Description);

public sealed record PaymentTransactionItem(Guid PaymentId, string PaymentName, decimal PaidAmount, string Currency, DateOnly PaidDate, string PaidPeriod, string? Comment);

public sealed record MonthlyStatisticsCurrency(
    string Currency,
    decimal PlannedAmount,
    decimal PaidAmount,
    decimal RemainingAmount,
    int PlannedCount,
    int PaidCount,
    int UnpaidCount);

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
            .Select(x => new PaymentListItem(x.Id, x.Name, x.Amount, x.Currency, x.NextPaymentDate!.Value, x.RecurrenceUnit, x.PaymentMethod, x.IsAutoDebit))
            .ToList();
    }

    public async Task<PaymentDetails?> GetDetailsAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await payments.FindForOwnerAsync(userId, paymentId, cancellationToken);
        return payment is null || !payment.IsActive || !payment.NextPaymentDate.HasValue
            ? null
            : new PaymentDetails(payment.Id, payment.Name, payment.Amount, payment.Currency, payment.RecurrenceInterval,
                payment.RecurrenceUnit, payment.NextPaymentDate.Value, payment.PaymentMethod, payment.IsAutoDebit, payment.Description);
    }

    public async Task<bool> UpdateAsync(Guid userId, Guid paymentId, PaymentDetails details, CancellationToken cancellationToken)
    {
        var payment = await payments.FindForOwnerAsync(userId, paymentId, cancellationToken);
        if (payment is null || !payment.IsActive || payment.Id != details.Id)
            return false;

        payment.UpdateDetails(details.Name, details.Amount, details.Currency, details.RecurrenceInterval, details.RecurrenceUnit,
            details.NextPaymentDate, details.PaymentMethod, details.IsAutoDebit, details.Description, DateTime.UtcNow);
        await payments.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeactivateAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await payments.FindForOwnerAsync(userId, paymentId, cancellationToken);
        if (payment is null || !payment.IsActive)
            return false;

        payment.Deactivate(DateTime.UtcNow);
        await payments.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<(bool Success, bool IsOneTime, DateOnly? NextPaymentDate)> RecordPaymentAsync(
        Guid userId,
        Guid paymentId,
        decimal paidAmount,
        DateOnly paidDate,
        string? comment,
        CancellationToken cancellationToken)
    {
        var payment = await payments.FindForOwnerAsync(userId, paymentId, cancellationToken);
        if (payment is null || !payment.IsActive || !payment.NextPaymentDate.HasValue)
            return (false, false, null);

        var dueDate = payment.NextPaymentDate.Value;
        payment.RecordPayment(paidAmount, paidDate, dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), comment, DateTime.UtcNow);
        await payments.SaveChangesAsync(cancellationToken);
        return (true, payment.RecurrenceUnit == RecurrenceUnit.Once, payment.NextPaymentDate);
    }

    public async Task<IReadOnlyList<PaymentTransactionItem>> GetHistoryAsync(Guid userId, Guid? paymentId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var entities = await payments.GetTransactionsForOwnerAsync(userId, paymentId, from, to, cancellationToken);
        return entities.OrderByDescending(x => x.PaidDate)
            .Select(x => new PaymentTransactionItem(x.RecurringPaymentId, x.RecurringPayment.Name, x.PaidAmount,
                x.Currency, x.PaidDate, x.PaidPeriod, x.Comment))
            .ToList();
    }

    public async Task<IReadOnlyList<MonthlyStatisticsCurrency>> GetMonthlyStatisticsAsync(Guid userId, int year, int month, CancellationToken cancellationToken)
    {
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var activePayments = await payments.GetActiveAsync(userId, null, null, cancellationToken);
        var transactions = await payments.GetTransactionsForOwnerAsync(userId, null, start, end, cancellationToken);
        var planned = new Dictionary<string, (decimal Amount, int Count)>(StringComparer.OrdinalIgnoreCase);
        var plannedOccurrences = new HashSet<(Guid PaymentId, DateOnly DueDate)>();
        foreach (var payment in activePayments)
        {
            if (!payment.NextPaymentDate.HasValue)
                continue;

            var dueDate = payment.NextPaymentDate.Value;
            while (dueDate < start)
            {
                if (payment.RecurrenceUnit == RecurrenceUnit.Once)
                {
                    dueDate = end.AddDays(1);
                    break;
                }
                dueDate = PaymentDateCalculator.CalculateNext(dueDate, payment.RecurrenceInterval, payment.RecurrenceUnit);
            }

            while (dueDate <= end)
            {
                var current = planned.GetValueOrDefault(payment.Currency);
                planned[payment.Currency] = (current.Amount + payment.Amount, current.Count + 1);
                plannedOccurrences.Add((payment.Id, dueDate));
                if (payment.RecurrenceUnit == RecurrenceUnit.Once)
                    break;
                dueDate = PaymentDateCalculator.CalculateNext(dueDate, payment.RecurrenceInterval, payment.RecurrenceUnit);
            }
        }

        // После оплаты NextPaymentDate уже сдвинут вперед. Восстанавливаем оплаченный
        // срок в плановой части месяца, чтобы план не исчезал после отметки оплаты.
        foreach (var transaction in transactions)
        {
            if (!DateOnly.TryParseExact(transaction.PaidPeriod, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate)
                || dueDate < start || dueDate > end || transaction.RecurringPayment is null
                || !plannedOccurrences.Add((transaction.RecurringPaymentId, dueDate)))
                continue;

            var current = planned.GetValueOrDefault(transaction.Currency);
            planned[transaction.Currency] = (current.Amount + transaction.ExpectedAmount, current.Count + 1);
        }

        var paid = transactions.GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => (Amount: x.Sum(t => t.PaidAmount), Count: x.Count()), StringComparer.OrdinalIgnoreCase);
        return planned.Keys.Union(paid.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .Select(currency =>
            {
                var plan = planned.GetValueOrDefault(currency);
                var actual = paid.GetValueOrDefault(currency);
                return new MonthlyStatisticsCurrency(currency, plan.Amount, actual.Amount,
                    Math.Max(0, plan.Amount - actual.Amount), plan.Count, actual.Count, Math.Max(0, plan.Count - actual.Count));
            })
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
            existing.Reset(ConversationKind.AddPayment, payload, DateTime.UtcNow);
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
                if (!UserInputParser.TryParsePositiveAmount(input, out var amount))
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

                states.Remove(state);
                await payments.CreateAsync(userId, draft.Name!, draft.Amount!.Value, draft.Currency!, 1, draft.RecurrenceUnit!.Value, draft.NextPaymentDate!.Value, cancellationToken);
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

public sealed class PaymentEditConversationService(
    IConversationStateRepository states,
    PaymentService payments)
{
    public async Task<string> BeginAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await payments.GetDetailsAsync(userId, paymentId, cancellationToken);
        if (payment is null)
            return "Платеж не найден или уже отключен.";

        var draft = PaymentEditDraft.From(payment);
        var existing = await states.FindAsync(userId, cancellationToken);
        var payload = JsonSerializer.Serialize(draft);
        if (existing is not null)
            existing.Reset(ConversationKind.EditPayment, payload, DateTime.UtcNow);
        else
            await states.AddAsync(ConversationState.Create(userId, ConversationKind.EditPayment, payload, DateTime.UtcNow), cancellationToken);
        await states.SaveChangesAsync(cancellationToken);
        return Prompt(draft);
    }

    public async Task<string?> HandleInputAsync(Guid userId, string input, CancellationToken cancellationToken)
    {
        var state = await states.FindAsync(userId, cancellationToken);
        if (state is null || state.Kind != ConversationKind.EditPayment)
            return null;

        input = input.Trim();
        if (input.Equals("отмена", StringComparison.OrdinalIgnoreCase) || input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            states.Remove(state);
            await states.SaveChangesAsync(cancellationToken);
            return "Редактирование отменено.";
        }

        var draft = JsonSerializer.Deserialize<PaymentEditDraft>(state.PayloadJson);
        if (draft is null)
            return "Не удалось восстановить редактирование. Запустите его заново через /edit.";

        switch (draft.Step)
        {
            case PaymentEditStep.Name:
                if (input != "-")
                {
                    if (string.IsNullOrWhiteSpace(input)) return "Название не может быть пустым. Повторите ввод:";
                    draft.Name = input;
                }
                draft.Step = PaymentEditStep.Amount;
                break;
            case PaymentEditStep.Amount:
                if (input != "-")
                {
                    if (!UserInputParser.TryParsePositiveAmount(input, out var amount))
                        return "Введите положительную сумму, например `35.50`, или `-`, чтобы оставить текущую:";
                    draft.Amount = amount;
                }
                draft.Step = PaymentEditStep.Currency;
                break;
            case PaymentEditStep.Currency:
                if (input != "-")
                {
                    if (input.Length != 3 || input.Any(x => !char.IsLetter(x)))
                        return "Введите трехбуквенный код валюты или `-`, чтобы оставить текущую:";
                    draft.Currency = input.ToUpperInvariant();
                }
                draft.Step = PaymentEditStep.Interval;
                break;
            case PaymentEditStep.Interval:
                if (input != "-")
                {
                    if (!int.TryParse(input, out var interval) || interval <= 0)
                        return "Введите положительный целочисленный интервал или `-`, чтобы оставить текущий:";
                    draft.RecurrenceInterval = interval;
                }
                draft.Step = PaymentEditStep.Recurrence;
                break;
            case PaymentEditStep.Recurrence:
                if (input != "-")
                {
                    if (!TryParseRecurrence(input, out var recurrenceUnit))
                        return "Введите weekly, monthly, yearly или once, либо `-`, чтобы оставить текущую:";
                    draft.RecurrenceUnit = recurrenceUnit;
                }
                draft.Step = PaymentEditStep.NextPaymentDate;
                break;
            case PaymentEditStep.NextPaymentDate:
                if (input != "-")
                {
                    if (!DateOnly.TryParseExact(input, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var nextDate))
                        return "Введите дату в формате ГГГГ-ММ-ДД или `-`, чтобы оставить текущую:";
                    draft.NextPaymentDate = nextDate;
                }
                draft.Step = PaymentEditStep.PaymentMethod;
                break;
            case PaymentEditStep.PaymentMethod:
                if (input != "-")
                {
                    if (!TryParsePaymentMethod(input, out var method))
                        return "Введите card, bank, cash или other, либо `-`, чтобы оставить текущий способ:";
                    draft.PaymentMethod = method;
                }
                draft.Step = PaymentEditStep.AutoDebit;
                break;
            case PaymentEditStep.AutoDebit:
                if (input != "-")
                {
                    if (!TryParseBoolean(input, out var autoDebit))
                        return "Введите да/нет или yes/no, либо `-`, чтобы оставить текущее значение:";
                    draft.IsAutoDebit = autoDebit;
                }
                draft.Step = PaymentEditStep.Description;
                break;
            case PaymentEditStep.Description:
                if (input != "-")
                    draft.Description = input;
                draft.Step = PaymentEditStep.Confirmation;
                break;
            case PaymentEditStep.Confirmation:
                if (input.Equals("нет", StringComparison.OrdinalIgnoreCase) || input.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    states.Remove(state);
                    await states.SaveChangesAsync(cancellationToken);
                    return "Изменения отменены.";
                }
                if (!input.Equals("да", StringComparison.OrdinalIgnoreCase) && !input.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    return "Введите `да` для сохранения или `нет` для отмены:";

                var updated = new PaymentDetails(draft.PaymentId, draft.Name!, draft.Amount!.Value, draft.Currency!, draft.RecurrenceInterval,
                    draft.RecurrenceUnit!.Value, draft.NextPaymentDate!.Value, draft.PaymentMethod!.Value, draft.IsAutoDebit!.Value, draft.Description);
                states.Remove(state);
                if (!await payments.UpdateAsync(userId, draft.PaymentId, updated, cancellationToken))
                    return "Платеж не найден или уже отключен.";
                return "Платеж обновлен. История оплат не изменена.";
        }

        state.UpdatePayload(JsonSerializer.Serialize(draft), DateTime.UtcNow);
        await states.SaveChangesAsync(cancellationToken);
        return Prompt(draft);
    }

    private static string Prompt(PaymentEditDraft draft) => draft.Step switch
    {
        PaymentEditStep.Name => $"Название ({draft.Name}). Введите новое или `-`, чтобы оставить текущее:",
        PaymentEditStep.Amount => $"Сумма ({draft.Amount:0.##}). Введите новую или `-`:",
        PaymentEditStep.Currency => $"Валюта ({draft.Currency}). Введите новую или `-`:",
        PaymentEditStep.Interval => $"Интервал ({draft.RecurrenceInterval}). Введите новый или `-`:",
        PaymentEditStep.Recurrence => $"Периодичность ({draft.RecurrenceUnit}). Введите weekly, monthly, yearly или once, либо `-`:",
        PaymentEditStep.NextPaymentDate => $"Следующая дата ({draft.NextPaymentDate:yyyy-MM-dd}). Введите новую в формате ГГГГ-ММ-ДД или `-`:",
        PaymentEditStep.PaymentMethod => $"Способ оплаты ({draft.PaymentMethod}). Введите card, bank, cash или other, либо `-`:",
        PaymentEditStep.AutoDebit => $"Автосписание ({(draft.IsAutoDebit == true ? "да" : "нет")}). Введите да/нет или `-`:",
        PaymentEditStep.Description => $"Комментарий ({draft.Description ?? "нет"}). Введите новый или `-`, чтобы оставить текущий:",
        PaymentEditStep.Confirmation => $"Проверьте изменения:\n{draft.Name} — {draft.Amount:0.##} {draft.Currency}\nДата: {draft.NextPaymentDate:yyyy-MM-dd}\nПериодичность: {draft.RecurrenceInterval} {draft.RecurrenceUnit}\nСпособ оплаты: {draft.PaymentMethod}\nАвтосписание: {(draft.IsAutoDebit == true ? "да" : "нет")}\nКомментарий: {draft.Description ?? "нет"}\n\nСохранить? Введите да или нет.",
        _ => "Введите новое значение или `отмена` для выхода."
    };

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

    private static bool TryParsePaymentMethod(string input, out PaymentMethod method)
    {
        method = input.ToLowerInvariant() switch
        {
            "card" or "карта" => PaymentMethod.Card,
            "bank" or "banktransfer" or "перевод" => PaymentMethod.BankTransfer,
            "cash" or "наличные" => PaymentMethod.Cash,
            "other" or "другое" => PaymentMethod.Other,
            _ => (PaymentMethod)(-1)
        };
        return method >= PaymentMethod.Card;
    }

    private static bool TryParseBoolean(string input, out bool value)
    {
        if (input.Equals("да", StringComparison.OrdinalIgnoreCase) || input.Equals("yes", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
        if (input.Equals("нет", StringComparison.OrdinalIgnoreCase) || input.Equals("no", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
        value = false;
        return false;
    }

    private sealed class PaymentEditDraft
    {
        public Guid PaymentId { get; set; }
        public PaymentEditStep Step { get; set; } = PaymentEditStep.Name;
        public string? Name { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
        public int RecurrenceInterval { get; set; }
        public RecurrenceUnit? RecurrenceUnit { get; set; }
        public DateOnly? NextPaymentDate { get; set; }
        public PaymentMethod? PaymentMethod { get; set; }
        public bool? IsAutoDebit { get; set; }
        public string? Description { get; set; }

        public static PaymentEditDraft From(PaymentDetails payment) => new()
        {
            PaymentId = payment.Id,
            Name = payment.Name,
            Amount = payment.Amount,
            Currency = payment.Currency,
            RecurrenceInterval = payment.RecurrenceInterval,
            RecurrenceUnit = payment.RecurrenceUnit,
            NextPaymentDate = payment.NextPaymentDate,
            PaymentMethod = payment.PaymentMethod,
            IsAutoDebit = payment.IsAutoDebit,
            Description = payment.Description
        };
    }

    private enum PaymentEditStep
    {
        Name,
        Amount,
        Currency,
        Interval,
        Recurrence,
        NextPaymentDate,
        PaymentMethod,
        AutoDebit,
        Description,
        Confirmation
    }
}

public sealed class PaymentRecordConversationService(
    IConversationStateRepository states,
    PaymentService payments)
{
    public async Task<string> BeginAsync(Guid userId, Guid paymentId, DateOnly localToday, CancellationToken cancellationToken)
    {
        var payment = await payments.GetDetailsAsync(userId, paymentId, cancellationToken);
        if (payment is null)
            return "Платеж не найден или уже отключен.";

        var draft = new PaymentRecordDraft
        {
            PaymentId = payment.Id,
            ExpectedAmount = payment.Amount,
            PaidAmount = payment.Amount,
            PaidDate = localToday,
            Step = PaymentRecordStep.Amount
        };
        var existing = await states.FindAsync(userId, cancellationToken);
        var payload = JsonSerializer.Serialize(draft);
        if (existing is not null)
            existing.Reset(ConversationKind.RecordPayment, payload, DateTime.UtcNow);
        else
            await states.AddAsync(ConversationState.Create(userId, ConversationKind.RecordPayment, payload, DateTime.UtcNow), cancellationToken);
        await states.SaveChangesAsync(cancellationToken);
        return $"Платеж: {payment.Name}. Введите фактическую сумму ({payment.Amount:0.##} {payment.Currency}) или `-`, чтобы использовать ожидаемую:";
    }

    public async Task<string?> HandleInputAsync(Guid userId, string input, CancellationToken cancellationToken)
    {
        var state = await states.FindAsync(userId, cancellationToken);
        if (state is null || state.Kind != ConversationKind.RecordPayment)
            return null;

        input = input.Trim();
        if (input.Equals("отмена", StringComparison.OrdinalIgnoreCase) || input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            states.Remove(state);
            await states.SaveChangesAsync(cancellationToken);
            return "Отметка оплаты отменена.";
        }

        var draft = JsonSerializer.Deserialize<PaymentRecordDraft>(state.PayloadJson);
        if (draft is null)
            return "Не удалось восстановить оплату. Запустите /pay заново.";

        switch (draft.Step)
        {
            case PaymentRecordStep.Amount:
                if (input != "-" && !UserInputParser.TryParsePositiveAmount(input, out _))
                    return "Введите положительную фактическую сумму или `-` для ожидаемой суммы:";
                if (input != "-")
                {
                    UserInputParser.TryParsePositiveAmount(input, out var amount);
                    draft.PaidAmount = amount;
                }
                draft.Step = PaymentRecordStep.Date;
                break;
            case PaymentRecordStep.Date:
                if (input != "-")
                {
                    if (!DateOnly.TryParseExact(input, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var paidDate))
                        return "Введите дату оплаты в формате ГГГГ-ММ-ДД или `-` для сегодняшней даты:";
                    draft.PaidDate = paidDate;
                }
                draft.Step = PaymentRecordStep.Comment;
                break;
            case PaymentRecordStep.Comment:
                draft.Comment = input == "-" ? null : input;
                draft.Step = PaymentRecordStep.Confirmation;
                break;
            case PaymentRecordStep.Confirmation:
                if (input.Equals("нет", StringComparison.OrdinalIgnoreCase) || input.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    states.Remove(state);
                    await states.SaveChangesAsync(cancellationToken);
                    return "Отметка оплаты отменена.";
                }
                if (!input.Equals("да", StringComparison.OrdinalIgnoreCase) && !input.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    return "Введите `да` для сохранения или `нет` для отмены:";

                states.Remove(state);
                var result = await payments.RecordPaymentAsync(userId, draft.PaymentId, draft.PaidAmount!.Value, draft.PaidDate!.Value, draft.Comment, cancellationToken);
                if (!result.Success)
                    return "Платеж не найден или уже отключен.";
                return result.IsOneTime
                    ? "Оплата сохранена. Однократный платеж завершен и отключен."
                    : $"Оплата сохранена. Следующая дата: {result.NextPaymentDate:yyyy-MM-dd}.";
        }

        state.UpdatePayload(JsonSerializer.Serialize(draft), DateTime.UtcNow);
        await states.SaveChangesAsync(cancellationToken);
        return draft.Step switch
        {
            PaymentRecordStep.Date => "Введите дату оплаты в формате ГГГГ-ММ-ДД или `-` для сегодняшней даты:",
            PaymentRecordStep.Comment => "Введите комментарий или `-`, если комментарий не нужен:",
            PaymentRecordStep.Confirmation => $"Проверьте оплату:\nСумма: {draft.PaidAmount:0.##}\nДата: {draft.PaidDate:yyyy-MM-dd}\nКомментарий: {draft.Comment ?? "нет"}\n\nСохранить? Введите да или нет.",
            _ => "Введите фактическую сумму или `-` для ожидаемой суммы:"
        };
    }

    private sealed class PaymentRecordDraft
    {
        public Guid PaymentId { get; set; }
        public decimal? ExpectedAmount { get; set; }
        public decimal? PaidAmount { get; set; }
        public DateOnly? PaidDate { get; set; }
        public string? Comment { get; set; }
        public PaymentRecordStep Step { get; set; }
    }

    private enum PaymentRecordStep
    {
        Amount,
        Date,
        Comment,
        Confirmation
    }
}
