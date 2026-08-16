using System.Globalization;
using System.Text.Json;
using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public interface IPaymentRepository
{
    Task AddAsync(RecurringPayment payment, CancellationToken cancellationToken);
    Task AddTransactionAsync(PaymentTransaction transaction, CancellationToken cancellationToken);
    Task<RecurringPayment?> FindForOwnerAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecurringPayment>> GetActiveAsync(Guid userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentTransaction>> GetTransactionsForOwnerAsync(Guid userId, Guid? paymentId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);
    Task<bool> HasTransactionForPeriodAsync(Guid userId, Guid paymentId, string paidPeriod, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ReminderCandidate>> GetReminderCandidatesAsync(CancellationToken cancellationToken);
    Task<int> ClearTransactionHistoryAsync(Guid userId, CancellationToken cancellationToken);
    Task<int> DeleteAllPaymentsAsync(Guid userId, CancellationToken cancellationToken);
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

public sealed record PaymentListItem(Guid Id, string Name, decimal Amount, string Currency, DateOnly DueDate, int RecurrenceInterval, RecurrenceUnit RecurrenceUnit, PaymentMethod PaymentMethod, bool IsAutoDebit);

public sealed record UpcomingPaymentItem(
    Guid Id,
    string Name,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    RecurrenceUnit RecurrenceUnit,
    PaymentMethod PaymentMethod,
    bool IsOverdue,
    int DaysFromToday);

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
    string? Description,
    int ScheduleDayOfMonth = 1,
    bool IsLastDayOfMonth = false);

public sealed record PaymentTransactionItem(Guid PaymentId, string PaymentName, decimal PaidAmount, string Currency, DateOnly PaidDate, string PaidPeriod, string? Comment);

public sealed record MonthlyStatisticsCurrency(
    string Currency,
    decimal PlannedAmount,
    decimal PaidAmount,
    decimal RemainingAmount,
    int PlannedCount,
    int PaidCount,
    int UnpaidCount);

public sealed record AnnualStatisticsCurrency(
    string Currency,
    decimal PlannedAmount,
    decimal PaidAmount,
    decimal RemainingAmount,
    int PlannedCount,
    int PaidCount,
    int UnpaidCount);

public enum PaymentRecordStatus
{
    Recorded,
    AlreadyRecorded,
    PaymentUnavailable,
    SaveConflict
}

public sealed record PaymentRecordResult(
    PaymentRecordStatus Status,
    bool IsOneTime = false,
    DateOnly? NextPaymentDate = null);

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
        CancellationToken cancellationToken,
        int? scheduleDayOfMonth = null,
        bool isLastDayOfMonth = false)
    {
        var payment = RecurringPayment.Create(userId, name, amount, currency, recurrenceInterval, recurrenceUnit, nextPaymentDate, DateTime.UtcNow,
            scheduleDayOfMonth, isLastDayOfMonth);
        await payments.AddAsync(payment, cancellationToken);
        await payments.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentListItem>> GetActiveAsync(Guid userId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var entities = await payments.GetActiveAsync(userId, from, to, cancellationToken);
        return entities
            .Where(x => x.NextPaymentDate.HasValue)
            .OrderBy(x => x.NextPaymentDate)
            .ThenBy(x => x.Name)
            .Select(x => new PaymentListItem(x.Id, x.Name, x.Amount, x.Currency, x.NextPaymentDate!.Value, x.RecurrenceInterval, x.RecurrenceUnit, x.PaymentMethod, x.IsAutoDebit))
            .ToList();
    }

    public async Task<IReadOnlyList<UpcomingPaymentItem>> GetUpcomingAsync(
        Guid userId,
        DateOnly today,
        int windowDays,
        CancellationToken cancellationToken)
    {
        var entities = await payments.GetActiveAsync(userId, null, null, cancellationToken);
        var ordered = entities
            .Where(x => x.NextPaymentDate.HasValue)
            .Select(x => new UpcomingPaymentItem(
                x.Id,
                x.Name,
                x.Amount,
                x.Currency,
                x.NextPaymentDate!.Value,
                x.RecurrenceUnit,
                x.PaymentMethod,
                x.NextPaymentDate.Value < today,
                x.NextPaymentDate.Value.DayNumber - today.DayNumber))
            .OrderBy(x => x.DueDate)
            .ThenBy(x => x.Name)
            .ToList();

        var result = ordered.Where(x => x.IsOverdue || x.DueDate <= today.AddDays(windowDays)).ToList();
        if (result.All(x => x.IsOverdue || x.DueDate <= today.AddDays(windowDays)))
        {
            var next = ordered.FirstOrDefault(x => !x.IsOverdue && x.DueDate > today.AddDays(windowDays));
            if (next is not null)
                result.Add(next);
        }

        return result;
    }

    public async Task<PaymentDetails?> GetDetailsAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await payments.FindForOwnerAsync(userId, paymentId, cancellationToken);
        return payment is null || !payment.IsActive || !payment.NextPaymentDate.HasValue
            ? null
            : new PaymentDetails(payment.Id, payment.Name, payment.Amount, payment.Currency, payment.RecurrenceInterval,
                payment.RecurrenceUnit, payment.NextPaymentDate.Value, payment.PaymentMethod, payment.IsAutoDebit, payment.Description,
                payment.ScheduleDayOfMonth, payment.IsLastDayOfMonth);
    }

    public async Task<bool> UpdateAsync(Guid userId, Guid paymentId, PaymentDetails details, CancellationToken cancellationToken)
    {
        var payment = await payments.FindForOwnerAsync(userId, paymentId, cancellationToken);
        if (payment is null || !payment.IsActive || payment.Id != details.Id)
            return false;

        payment.UpdateDetails(details.Name, details.Amount, details.Currency, details.RecurrenceInterval, details.RecurrenceUnit,
            details.NextPaymentDate, details.PaymentMethod, details.IsAutoDebit, details.Description, DateTime.UtcNow,
            details.ScheduleDayOfMonth, details.IsLastDayOfMonth);
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

    public async Task<PaymentRecordResult> RecordPaymentAsync(
        Guid userId,
        Guid paymentId,
        decimal paidAmount,
        DateOnly paidDate,
        string? comment,
        CancellationToken cancellationToken)
    {
        var payment = await payments.FindForOwnerAsync(userId, paymentId, cancellationToken);
        if (payment is null || !payment.NextPaymentDate.HasValue)
            return new PaymentRecordResult(PaymentRecordStatus.PaymentUnavailable);

        var dueDate = payment.NextPaymentDate.Value;
        var paidPeriod = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (await payments.HasTransactionForPeriodAsync(userId, paymentId, paidPeriod, cancellationToken))
            return new PaymentRecordResult(PaymentRecordStatus.AlreadyRecorded, payment.RecurrenceUnit == RecurrenceUnit.Once, payment.NextPaymentDate);
        if (!payment.IsActive)
            return new PaymentRecordResult(PaymentRecordStatus.PaymentUnavailable);

        var transaction = payment.RecordPayment(paidAmount, paidDate, paidPeriod, comment, DateTime.UtcNow);
        await payments.AddTransactionAsync(transaction, cancellationToken);
        try
        {
            await payments.SaveChangesAsync(cancellationToken);
        }
        catch (PaymentConcurrencyException)
        {
            var wasRecorded = await payments.HasTransactionForPeriodAsync(userId, paymentId, paidPeriod, cancellationToken);
            return new PaymentRecordResult(
                wasRecorded ? PaymentRecordStatus.AlreadyRecorded : PaymentRecordStatus.SaveConflict,
                payment.RecurrenceUnit == RecurrenceUnit.Once,
                payment.NextPaymentDate);
        }
        return new PaymentRecordResult(PaymentRecordStatus.Recorded, payment.RecurrenceUnit == RecurrenceUnit.Once, payment.NextPaymentDate);
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

    public async Task<IReadOnlyList<AnnualStatisticsCurrency>> GetAnnualStatisticsAsync(Guid userId, int year, CancellationToken cancellationToken)
    {
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);
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
                return new AnnualStatisticsCurrency(currency, plan.Amount, actual.Amount,
                    Math.Max(0, plan.Amount - actual.Amount), plan.Count, actual.Count, Math.Max(0, plan.Count - actual.Count));
            })
            .ToList();
    }
}

public sealed class PaymentConversationService(
    IConversationStateRepository states,
    PaymentService payments)
{
    public async Task<string> BeginAsync(Guid userId, string defaultCurrency, DateOnly localToday, CancellationToken cancellationToken)
    {
        var existing = await states.FindAsync(userId, cancellationToken);
        var payload = JsonSerializer.Serialize(new PaymentDraftState { Currency = defaultCurrency, Today = localToday });
        if (existing is not null)
            existing.Reset(ConversationKind.AddPayment, payload, DateTime.UtcNow);
        else
            await states.AddAsync(ConversationState.Create(userId, ConversationKind.AddPayment, payload, DateTime.UtcNow), cancellationToken);
        await states.SaveChangesAsync(cancellationToken);
        return "➕ Новый платеж\n\nКак называется платеж?";
    }

    public async Task<string?> HandleInputAsync(Guid userId, string input, CancellationToken cancellationToken)
    {
        var state = await states.FindAsync(userId, cancellationToken);
        if (state is null || state.Kind != ConversationKind.AddPayment)
            return null;

        var draft = JsonSerializer.Deserialize<PaymentDraftState>(state.PayloadJson) ?? new PaymentDraftState();
        input = input.Trim();
        if (input.Equals("отмена", StringComparison.OrdinalIgnoreCase) || input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            states.Remove(state);
            await states.SaveChangesAsync(cancellationToken);
            return "Создание платежа отменено.";
        }
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
                draft.Step = PaymentDraftStep.Recurrence;
                break;
            case PaymentDraftStep.Recurrence:
                if (!TryParseRecurrence(input, out var recurrenceUnit))
                    return "Выберите периодичность кнопкой ниже или введите её по-русски:";
                draft.RecurrenceUnit = recurrenceUnit;
                draft.Step = recurrenceUnit == RecurrenceUnit.Once ? PaymentDraftStep.NextPaymentDate : PaymentDraftStep.Schedule;
                break;
            case PaymentDraftStep.Schedule:
                if (draft.RecurrenceUnit == RecurrenceUnit.Year)
                {
                    if (!TryParseUserDate(input, draft.Today, out var annualDate, out var error))
                        return error;
                    draft.NextPaymentDate = annualDate;
                    draft.ScheduleDayOfMonth = annualDate.Day;
                    draft.Step = PaymentDraftStep.Confirmation;
                    break;
                }

                if (draft.RecurrenceUnit == RecurrenceUnit.Month && TryParseMonthDay(input, out var monthDay, out var lastDay))
                {
                    draft.ScheduleDayOfMonth = monthDay;
                    draft.IsLastDayOfMonth = lastDay;
                    draft.NextPaymentDate = NextMonthlyDate(draft.Today, monthDay, lastDay);
                    draft.Step = PaymentDraftStep.Confirmation;
                    break;
                }

                if (draft.RecurrenceUnit == RecurrenceUnit.Week && TryParseWeekday(input, out var weekday))
                {
                    draft.NextPaymentDate = NextWeekday(draft.Today, weekday);
                    draft.Step = PaymentDraftStep.Confirmation;
                    break;
                }

                return "Выберите вариант расписания кнопкой ниже:";
            case PaymentDraftStep.NextPaymentDate:
                if (DateShortcutCalculator.TryParse(input, draft.Today, out var shortcutDate))
                {
                    draft.NextPaymentDate = shortcutDate;
                    draft.Step = PaymentDraftStep.Confirmation;
                    break;
                }
                if (!DateOnly.TryParseExact(input, new[] { "dd.MM.yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var nextDate))
                    return "Не получилось распознать дату. Введите, например: 26.09.2026";
                if (nextDate < draft.Today)
                    return $"Дата не может быть в прошлом. Укажите дату начиная с {draft.Today:dd.MM.yyyy}:";
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

                await payments.CreateAsync(userId, draft.Name!, draft.Amount!.Value, draft.Currency!, 1, draft.RecurrenceUnit!.Value,
                    draft.NextPaymentDate!.Value, cancellationToken, draft.ScheduleDayOfMonth, draft.IsLastDayOfMonth);
                states.Remove(state);
                await states.SaveChangesAsync(cancellationToken);
                return "Платеж сохранен. Используйте /payments для просмотра.";
        }

        state.UpdatePayload(JsonSerializer.Serialize(draft), DateTime.UtcNow);
        await states.SaveChangesAsync(cancellationToken);
        return draft.Step switch
        {
            PaymentDraftStep.Amount => "Введите сумму:",
            PaymentDraftStep.Recurrence => "Как часто нужно платить?",
            PaymentDraftStep.Schedule when draft.RecurrenceUnit == RecurrenceUnit.Month => "📅 В какой день месяца платить?",
            PaymentDraftStep.Schedule when draft.RecurrenceUnit == RecurrenceUnit.Week => "📅 В какой день недели платить?",
            PaymentDraftStep.Schedule when draft.RecurrenceUnit == RecurrenceUnit.Year => "📅 Введите дату ежегодного платежа, например 15.09.2026:",
            PaymentDraftStep.NextPaymentDate => "📅 Когда следующий платеж? Выберите кнопку или введите дату, например 26.09.2026:",
            PaymentDraftStep.Confirmation => $"Проверьте платеж:\n\n{draft.Name}\n{draft.Amount:0.##} {draft.Currency}\n{PaymentDisplayNames.Recurrence(1, draft.RecurrenceUnit!.Value)}\nСледующий платеж: {draft.NextPaymentDate:dd.MM.yyyy}\n\nСохранить платеж?",
            _ => "Введите название платежа:"
        };
    }

    private static bool TryParseRecurrence(string input, out RecurrenceUnit unit)
    {
        unit = input.ToLowerInvariant() switch
        {
            "weekly" or "еженедельно" or "неделя" or "каждую неделю" => RecurrenceUnit.Week,
            "monthly" or "ежемесячно" or "месяц" or "каждый месяц" => RecurrenceUnit.Month,
            "yearly" or "annually" or "ежегодно" or "год" or "каждый год" => RecurrenceUnit.Year,
            "once" or "однократно" or "один раз" or "разовый платеж" => RecurrenceUnit.Once,
            _ => (RecurrenceUnit)(-1)
        };
        return unit >= RecurrenceUnit.Once;
    }

    private static bool TryParseUserDate(string input, DateOnly today, out DateOnly date, out string error)
    {
        if (!DateOnly.TryParseExact(input, new[] { "dd.MM.yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            error = "Не получилось распознать дату. Введите, например: 15.09.2026";
            return false;
        }
        if (date < today)
        {
            error = $"Дата не может быть в прошлом. Укажите дату начиная с {today:dd.MM.yyyy}:";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool TryParseMonthDay(string input, out int day, out bool lastDay)
    {
        lastDay = input.Trim().Equals("последний день", StringComparison.OrdinalIgnoreCase)
            || input.Trim().Equals("последний день месяца", StringComparison.OrdinalIgnoreCase);
        if (lastDay)
        {
            day = 31;
            return true;
        }
        return int.TryParse(input.Trim(), out day) && day is >= 1 and <= 31;
    }

    private static bool TryParseWeekday(string input, out DayOfWeek weekday)
    {
        weekday = input.Trim().ToLowerInvariant() switch
        {
            "понедельник" => DayOfWeek.Monday,
            "вторник" => DayOfWeek.Tuesday,
            "среда" => DayOfWeek.Wednesday,
            "четверг" => DayOfWeek.Thursday,
            "пятница" => DayOfWeek.Friday,
            "суббота" => DayOfWeek.Saturday,
            "воскресенье" => DayOfWeek.Sunday,
            _ => (DayOfWeek)(-1)
        };
        return weekday >= DayOfWeek.Sunday && weekday <= DayOfWeek.Saturday;
    }

    private static DateOnly NextMonthlyDate(DateOnly today, int day, bool lastDay)
    {
        var candidate = new DateOnly(today.Year, today.Month, lastDay ? DateTime.DaysInMonth(today.Year, today.Month) : Math.Min(day, DateTime.DaysInMonth(today.Year, today.Month)));
        return candidate < today
            ? new DateOnly(today.AddMonths(1).Year, today.AddMonths(1).Month, lastDay ? DateTime.DaysInMonth(today.AddMonths(1).Year, today.AddMonths(1).Month) : Math.Min(day, DateTime.DaysInMonth(today.AddMonths(1).Year, today.AddMonths(1).Month)))
            : candidate;
    }

    private static DateOnly NextWeekday(DateOnly today, DayOfWeek weekday)
    {
        var days = ((int)weekday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(days);
    }

    private sealed class PaymentDraftState
    {
        public PaymentDraftStep Step { get; set; }
        public string? Name { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
        public RecurrenceUnit? RecurrenceUnit { get; set; }
        public DateOnly? NextPaymentDate { get; set; }
        public int ScheduleDayOfMonth { get; set; } = 1;
        public bool IsLastDayOfMonth { get; set; }
        public DateOnly Today { get; set; }
    }

    private enum PaymentDraftStep
    {
        Name = 0,
        Amount = 1,
        Recurrence = 2,
        Schedule = 3,
        NextPaymentDate = 4,
        Confirmation = 5
    }
}

public sealed class PaymentEditConversationService(
    IConversationStateRepository states,
    PaymentService payments)
{
    public async Task<string> BeginAsync(Guid userId, Guid paymentId, DateOnly localToday, CancellationToken cancellationToken)
    {
        var payment = await payments.GetDetailsAsync(userId, paymentId, cancellationToken);
        if (payment is null)
            return "Платеж не найден или уже отключен.";

        var draft = PaymentEditDraft.From(payment, localToday);
        draft.Step = PaymentEditStep.Menu;
        var existing = await states.FindAsync(userId, cancellationToken);
        var payload = JsonSerializer.Serialize(draft);
        if (existing is not null)
            existing.Reset(ConversationKind.EditPayment, payload, DateTime.UtcNow);
        else
            await states.AddAsync(ConversationState.Create(userId, ConversationKind.EditPayment, payload, DateTime.UtcNow), cancellationToken);
        await states.SaveChangesAsync(cancellationToken);
        return Prompt(draft);
    }

    public async Task<string> BeginFieldAsync(Guid userId, Guid paymentId, string field, DateOnly localToday, CancellationToken cancellationToken)
    {
        var payment = await payments.GetDetailsAsync(userId, paymentId, cancellationToken);
        if (payment is null)
            return "Платеж не найден или уже отключен.";

        var draft = PaymentEditDraft.From(payment, localToday);
        draft.Step = PaymentEditStep.Menu;
        draft.Step = field.ToLowerInvariant() switch
        {
            "amount" => PaymentEditStep.FieldAmount,
            "schedule" => PaymentEditStep.FieldScheduleRecurrence,
            "method" => PaymentEditStep.FieldMethod,
            "autopay" => PaymentEditStep.FieldAutoDebit,
            "name" => PaymentEditStep.FieldName,
            "comment" => PaymentEditStep.FieldDescription,
            _ => PaymentEditStep.Menu
        };
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

        var keepCurrent = input == "-"
            || input.Equals("оставить текущее", StringComparison.OrdinalIgnoreCase)
            || input.Equals("оставить текущий", StringComparison.OrdinalIgnoreCase)
            || input.Equals("оставить текущую", StringComparison.OrdinalIgnoreCase);

        switch (draft.Step)
        {
            case PaymentEditStep.Menu:
                // Compatibility with the previous sequential flow.
                draft.Step = keepCurrent ? PaymentEditStep.Amount : PaymentEditStep.Name;
                break;
            case PaymentEditStep.FieldAmount:
                if (!UserInputParser.TryParsePositiveAmount(input, out var fieldAmount))
                    return "Введите положительную сумму, например 2 000 или 2000,50:";
                draft.Amount = fieldAmount;
                return await SaveFieldAsync(userId, state, draft, cancellationToken);
            case PaymentEditStep.FieldSchedule:
                if (!DateOnly.TryParseExact(input, new[] { "dd.MM.yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fieldDate))
                    return "Введите дату в формате ДД.ММ.ГГГГ, например 26.09.2026:";
                if (fieldDate < draft.Today)
                    return $"Дата не может быть в прошлом. Укажите дату начиная с {draft.Today:dd.MM.yyyy}:";
                draft.NextPaymentDate = fieldDate;
                return await SaveFieldAsync(userId, state, draft, cancellationToken);
            case PaymentEditStep.FieldScheduleRecurrence:
                if (!TryParseRecurrence(input, out var fieldRecurrence))
                    return "Выберите периодичность кнопкой ниже:";
                draft.RecurrenceUnit = fieldRecurrence;
                draft.Step = PaymentEditStep.FieldSchedule;
                state.UpdatePayload(JsonSerializer.Serialize(draft), DateTime.UtcNow);
                await states.SaveChangesAsync(cancellationToken);
                return Prompt(draft);
            case PaymentEditStep.FieldMethod:
                if (!TryParsePaymentMethod(input, out var fieldMethod))
                    return "Выберите способ оплаты: карта, банковский перевод, наличные или другое.";
                draft.PaymentMethod = fieldMethod;
                return await SaveFieldAsync(userId, state, draft, cancellationToken);
            case PaymentEditStep.FieldAutoDebit:
                if (!TryParseBoolean(input, out var fieldAutoDebit))
                    return "Выберите «Да» или «Нет»:";
                draft.IsAutoDebit = fieldAutoDebit;
                return await SaveFieldAsync(userId, state, draft, cancellationToken);
            case PaymentEditStep.FieldName:
                if (string.IsNullOrWhiteSpace(input))
                    return "Название не может быть пустым. Введите новое название:";
                draft.Name = input;
                return await SaveFieldAsync(userId, state, draft, cancellationToken);
            case PaymentEditStep.FieldDescription:
                draft.Description = input.Equals("без комментария", StringComparison.OrdinalIgnoreCase) ? null : input;
                return await SaveFieldAsync(userId, state, draft, cancellationToken);
            case PaymentEditStep.Name:
                if (!keepCurrent)
                {
                    if (string.IsNullOrWhiteSpace(input)) return "Название не может быть пустым. Повторите ввод:";
                    draft.Name = input;
                }
                draft.Step = PaymentEditStep.Amount;
                break;
            case PaymentEditStep.Amount:
                if (!keepCurrent)
                {
                    if (!UserInputParser.TryParsePositiveAmount(input, out var amount))
                        return "Введите положительную сумму, например 35,50, или нажмите «Оставить текущее»:";
                    draft.Amount = amount;
                }
                draft.Step = PaymentEditStep.Currency;
                break;
            case PaymentEditStep.Currency:
                if (!keepCurrent)
                {
                    if (input.Length != 3 || input.Any(x => !char.IsLetter(x)))
                        return "Введите трехбуквенный код валюты или нажмите «Оставить текущее»:";
                    draft.Currency = input.ToUpperInvariant();
                }
                draft.Step = PaymentEditStep.Interval;
                break;
            case PaymentEditStep.Interval:
                if (!keepCurrent)
                {
                    if (!int.TryParse(input, out var interval) || interval <= 0)
                        return "Введите положительный целочисленный интервал или нажмите «Оставить текущее»:";
                    draft.RecurrenceInterval = interval;
                }
                draft.Step = PaymentEditStep.Recurrence;
                break;
            case PaymentEditStep.Recurrence:
                if (!keepCurrent)
                {
                    if (!TryParseRecurrence(input, out var recurrenceUnit))
                        return "Выберите периодичность или нажмите «Оставить текущее»:";
                    draft.RecurrenceUnit = recurrenceUnit;
                }
                draft.Step = PaymentEditStep.NextPaymentDate;
                break;
            case PaymentEditStep.NextPaymentDate:
                if (!keepCurrent)
                {
                    DateOnly nextDate;
                    if (DateShortcutCalculator.TryParse(input, draft.Today, out var shortcutDate))
                        nextDate = shortcutDate;
                    else
                    {
                        var formats = new[] { "dd.MM.yyyy", "yyyy-MM-dd" };
                        if (!DateOnly.TryParseExact(input, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out nextDate))
                            return "Введите дату в формате ДД.ММ.ГГГГ, выберите ее кнопкой или нажмите «Оставить текущее»:";
                    }
                    if (nextDate < draft.Today)
                        return $"Дата не может быть в прошлом. Укажите дату начиная с {draft.Today:dd.MM.yyyy}:";
                    draft.NextPaymentDate = nextDate;
                }
                draft.Step = PaymentEditStep.PaymentMethod;
                break;
            case PaymentEditStep.PaymentMethod:
                if (!keepCurrent)
                {
                    if (!TryParsePaymentMethod(input, out var method))
                        return "Выберите способ оплаты или нажмите «Оставить текущее»:";
                    draft.PaymentMethod = method;
                }
                draft.Step = PaymentEditStep.AutoDebit;
                break;
            case PaymentEditStep.AutoDebit:
                if (!keepCurrent)
                {
                    if (!TryParseBoolean(input, out var autoDebit))
                        return "Выберите «Да», «Нет» или «Оставить текущее»:";
                    draft.IsAutoDebit = autoDebit;
                }
                draft.Step = PaymentEditStep.Description;
                break;
            case PaymentEditStep.Description:
                if (!keepCurrent)
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
                    return "Выберите «Да» для сохранения или «Нет» для отмены:";

                var updated = new PaymentDetails(draft.PaymentId, draft.Name!, draft.Amount!.Value, draft.Currency!, draft.RecurrenceInterval,
                    draft.RecurrenceUnit!.Value, draft.NextPaymentDate!.Value, draft.PaymentMethod!.Value, draft.IsAutoDebit!.Value, draft.Description,
                    draft.ScheduleDayOfMonth, draft.IsLastDayOfMonth);
                if (!await payments.UpdateAsync(userId, draft.PaymentId, updated, cancellationToken))
                    return "Платеж не найден или уже отключен.";
                states.Remove(state);
                await states.SaveChangesAsync(cancellationToken);
                return "Платеж обновлен. История оплат не изменена.";
        }

        state.UpdatePayload(JsonSerializer.Serialize(draft), DateTime.UtcNow);
        await states.SaveChangesAsync(cancellationToken);
        return Prompt(draft);
    }

    private static string Prompt(PaymentEditDraft draft) => draft.Step switch
    {
        PaymentEditStep.Menu => $"✏️ {draft.Name}\n\n{TelegramPresentationForEdit(draft)}\n\nЧто изменить?",
        PaymentEditStep.FieldName => "Введите новое название:",
        PaymentEditStep.FieldAmount => $"Текущая сумма: {draft.Amount:0.##} {draft.Currency}\n\nВведите новую сумму:",
        PaymentEditStep.FieldScheduleRecurrence => "Как часто нужно платить?",
        PaymentEditStep.FieldSchedule => $"Периодичность: {PaymentDisplayNames.Recurrence(draft.RecurrenceInterval, draft.RecurrenceUnit!.Value)}\n\nВведите следующую дату в формате ДД.ММ.ГГГГ:",
        PaymentEditStep.FieldMethod => "Выберите новый способ оплаты:",
        PaymentEditStep.FieldAutoDebit => "Автосписание включено?",
        PaymentEditStep.FieldDescription => "Введите комментарий или нажмите «Без комментария»:",
        PaymentEditStep.Name => $"Название: {draft.Name}. Введите новое или нажмите «Оставить текущее»:",
        PaymentEditStep.Amount => $"Сумма: {draft.Amount:0.##}. Введите новую или нажмите «Оставить текущее»:",
        PaymentEditStep.Currency => $"Валюта: {draft.Currency}. Введите новую или нажмите «Оставить текущее»:",
        PaymentEditStep.Interval => $"Интервал: {draft.RecurrenceInterval}. Введите новый или нажмите «Оставить текущее»:",
        PaymentEditStep.Recurrence => $"Периодичность: {PaymentDisplayNames.Recurrence(draft.RecurrenceUnit!.Value)}. Выберите новую или нажмите «Оставить текущее»:",
        PaymentEditStep.NextPaymentDate => $"Следующая дата: {draft.NextPaymentDate:dd.MM.yyyy}. Введите новую дату в формате ДД.ММ.ГГГГ, выберите кнопку или нажмите «Оставить текущее»:",
        PaymentEditStep.PaymentMethod => $"Способ оплаты: {PaymentDisplayNames.PaymentMethod(draft.PaymentMethod!.Value)}. Выберите новый или нажмите «Оставить текущее»:",
        PaymentEditStep.AutoDebit => $"Автосписание: {(draft.IsAutoDebit == true ? "да" : "нет")}. Выберите значение или нажмите «Оставить текущее»:",
        PaymentEditStep.Description => $"Комментарий: {draft.Description ?? "нет"}. Введите новый или нажмите «Оставить текущее»:",
        PaymentEditStep.Confirmation => $"Проверьте изменения:\n{draft.Name} — {draft.Amount:0.##} {draft.Currency}\nДата: {draft.NextPaymentDate:dd.MM.yyyy}\nПериодичность: {draft.RecurrenceInterval} {PaymentDisplayNames.Recurrence(draft.RecurrenceUnit!.Value)}\nСпособ оплаты: {PaymentDisplayNames.PaymentMethod(draft.PaymentMethod!.Value)}\nАвтосписание: {(draft.IsAutoDebit == true ? "да" : "нет")}\nКомментарий: {draft.Description ?? "нет"}\n\nСохранить изменения?",
        _ => "Введите новое значение или выберите «Отмена»."
    };

    private async Task<string> SaveFieldAsync(Guid userId, ConversationState state, PaymentEditDraft draft, CancellationToken cancellationToken)
    {
        var updated = new PaymentDetails(draft.PaymentId, draft.Name!, draft.Amount!.Value, draft.Currency!, draft.RecurrenceInterval,
            draft.RecurrenceUnit!.Value, draft.NextPaymentDate!.Value, draft.PaymentMethod!.Value, draft.IsAutoDebit!.Value, draft.Description,
            draft.ScheduleDayOfMonth, draft.IsLastDayOfMonth);
        if (!await payments.UpdateAsync(userId, draft.PaymentId, updated, cancellationToken))
            return "Платеж не найден или уже отключен.";
        states.Remove(state);
        await states.SaveChangesAsync(cancellationToken);
        return $"✅ {draft.Name} обновлен. История оплат не изменена.";
    }

    private static string TelegramPresentationForEdit(PaymentEditDraft draft) =>
        $"{draft.Amount:0.##} {draft.Currency}\n{PaymentDisplayNames.Recurrence(draft.RecurrenceInterval, draft.RecurrenceUnit!.Value)}, {draft.NextPaymentDate:dd.MM.yyyy}\n" +
        $"Способ оплаты: {PaymentDisplayNames.PaymentMethod(draft.PaymentMethod!.Value)}\nАвтосписание: {(draft.IsAutoDebit == true ? "включено" : "выключено")}\nКомментарий: {draft.Description ?? "—"}";

    private static bool TryParseRecurrence(string input, out RecurrenceUnit unit)
    {
        unit = input.ToLowerInvariant() switch
        {
            "weekly" or "еженедельно" or "неделя" or "каждую неделю" => RecurrenceUnit.Week,
            "monthly" or "ежемесячно" or "месяц" or "каждый месяц" => RecurrenceUnit.Month,
            "yearly" or "annually" or "ежегодно" or "год" or "каждый год" => RecurrenceUnit.Year,
            "once" or "однократно" or "один раз" or "разовый платеж" => RecurrenceUnit.Once,
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
        public int ScheduleDayOfMonth { get; set; } = 1;
        public bool IsLastDayOfMonth { get; set; }
        public PaymentMethod? PaymentMethod { get; set; }
        public bool? IsAutoDebit { get; set; }
        public string? Description { get; set; }

        public DateOnly Today { get; set; }

        public static PaymentEditDraft From(PaymentDetails payment, DateOnly localToday) => new()
        {
            PaymentId = payment.Id,
            Name = payment.Name,
            Amount = payment.Amount,
            Currency = payment.Currency,
            RecurrenceInterval = payment.RecurrenceInterval,
            RecurrenceUnit = payment.RecurrenceUnit,
            NextPaymentDate = payment.NextPaymentDate,
            ScheduleDayOfMonth = payment.ScheduleDayOfMonth,
            IsLastDayOfMonth = payment.IsLastDayOfMonth,
            PaymentMethod = payment.PaymentMethod,
            IsAutoDebit = payment.IsAutoDebit,
            Description = payment.Description,
            Today = localToday
        };
    }

    private enum PaymentEditStep
    {
        Menu,
        Name,
        Amount,
        Currency,
        Interval,
        Recurrence,
        NextPaymentDate,
        PaymentMethod,
        AutoDebit,
        Description,
        Confirmation,
        FieldAmount,
        FieldScheduleRecurrence,
        FieldSchedule,
        FieldMethod,
        FieldAutoDebit,
        FieldName,
        FieldDescription
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
            Today = localToday,
            Step = PaymentRecordStep.Amount
        };
        var existing = await states.FindAsync(userId, cancellationToken);
        var payload = JsonSerializer.Serialize(draft);
        if (existing is not null)
            existing.Reset(ConversationKind.RecordPayment, payload, DateTime.UtcNow);
        else
            await states.AddAsync(ConversationState.Create(userId, ConversationKind.RecordPayment, payload, DateTime.UtcNow), cancellationToken);
        await states.SaveChangesAsync(cancellationToken);
        return $"Платеж: {payment.Name}\nОжидаемая сумма: {payment.Amount:0.##} {payment.Currency}\n\nВведите другую сумму или нажмите «Ожидаемая сумма»:";
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
                var useExpectedAmount = input == "-" || input.Equals("ожидаемая сумма", StringComparison.OrdinalIgnoreCase);
                if (!useExpectedAmount && !UserInputParser.TryParsePositiveAmount(input, out _))
                    return "Введите положительную сумму или нажмите «Ожидаемая сумма»:";
                if (!useExpectedAmount)
                {
                    UserInputParser.TryParsePositiveAmount(input, out var amount);
                    draft.PaidAmount = amount;
                }
                draft.Step = PaymentRecordStep.Confirmation;
                break;
            case PaymentRecordStep.Date:
                if (DateShortcutCalculator.TryParse(input, draft.Today, out var paidShortcutDate))
                    draft.PaidDate = paidShortcutDate;
                else if (input != "-")
                {
                    var formats = new[] { "dd.MM.yyyy", "yyyy-MM-dd" };
                    if (!DateOnly.TryParseExact(input, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var paidDate))
                        return "Введите дату оплаты в формате ДД.ММ.ГГГГ или выберите дату кнопкой:";
                    draft.PaidDate = paidDate;
                }
                draft.Step = PaymentRecordStep.Confirmation;
                break;
            case PaymentRecordStep.Comment:
                draft.Comment = input == "-" || input.Equals("без комментария", StringComparison.OrdinalIgnoreCase) ? null : input;
                draft.Step = PaymentRecordStep.Confirmation;
                break;
            case PaymentRecordStep.Confirmation:
                if (input.Equals("изменить дату", StringComparison.OrdinalIgnoreCase))
                {
                    draft.Step = PaymentRecordStep.Date;
                    break;
                }
                if (input.Equals("добавить комментарий", StringComparison.OrdinalIgnoreCase))
                {
                    draft.Step = PaymentRecordStep.Comment;
                    break;
                }
                if (input.Equals("нет", StringComparison.OrdinalIgnoreCase) || input.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    states.Remove(state);
                    await states.SaveChangesAsync(cancellationToken);
                    return "Отметка оплаты отменена.";
                }
                if (!input.Equals("да", StringComparison.OrdinalIgnoreCase)
                    && !input.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    && !input.Equals("сохранить", StringComparison.OrdinalIgnoreCase))
                    return "Выберите «Сохранить» или измените дату/комментарий:";

                var result = await payments.RecordPaymentAsync(userId, draft.PaymentId, draft.PaidAmount!.Value, draft.PaidDate!.Value, draft.Comment, cancellationToken);
                if (result.Status == PaymentRecordStatus.PaymentUnavailable)
                    return "Этот платеж больше недоступен. Возможно, он был отключен. Запустите /pay и выберите другой платеж.";
                if (result.Status == PaymentRecordStatus.SaveConflict)
                    return "Не удалось сохранить оплату из-за изменения данных. Проверьте платеж и нажмите «Да» еще раз.";

                states.Remove(state);
                await states.SaveChangesAsync(cancellationToken);
                if (result.Status == PaymentRecordStatus.AlreadyRecorded)
                    return "Оплата уже сохранена. Повторная запись не создана.";
                return result.IsOneTime
                    ? "Оплата сохранена. Однократный платеж завершен и отключен."
                    : $"Оплата сохранена. Следующая дата: {result.NextPaymentDate:dd.MM.yyyy}.";
        }

        state.UpdatePayload(JsonSerializer.Serialize(draft), DateTime.UtcNow);
        await states.SaveChangesAsync(cancellationToken);
        return draft.Step switch
        {
            PaymentRecordStep.Date => "Выберите новую дату оплаты кнопкой или введите ее в формате ДД.ММ.ГГГГ:",
            PaymentRecordStep.Comment => "Добавьте комментарий или нажмите «Без комментария»:",
            PaymentRecordStep.Confirmation => $"Проверьте оплату:\nСумма: {draft.PaidAmount:0.##}\nДата: {draft.PaidDate:dd.MM.yyyy}\nКомментарий: {draft.Comment ?? "без комментария"}\n\nСохранить оплату или изменить данные?",
            _ => "Введите фактическую сумму или нажмите «Ожидаемая сумма»:"
        };
    }

    private sealed class PaymentRecordDraft
    {
        public Guid PaymentId { get; set; }
        public decimal? ExpectedAmount { get; set; }
        public decimal? PaidAmount { get; set; }
        public DateOnly? PaidDate { get; set; }
        public string? Comment { get; set; }
        public DateOnly Today { get; set; }
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
