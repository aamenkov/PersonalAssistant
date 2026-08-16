using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public static class PaymentDisplayNames
{
    public static string Recurrence(int interval, RecurrenceUnit unit) => unit switch
    {
        RecurrenceUnit.Week => interval == 1 ? "каждую неделю" : $"каждые {interval} недели",
        RecurrenceUnit.Month => interval == 1 ? "каждый месяц" : $"каждые {interval} месяца",
        RecurrenceUnit.Year => interval == 1 ? "каждый год" : $"каждые {interval} года",
        RecurrenceUnit.Once => "разовый платеж",
        _ => "расписание не указано"
    };

    public static string Recurrence(RecurrenceUnit unit) => unit switch
    {
        RecurrenceUnit.Week => "еженедельно",
        RecurrenceUnit.Month => "ежемесячно",
        RecurrenceUnit.Year => "ежегодно",
        RecurrenceUnit.Once => "однократно",
        _ => "не указано"
    };

    public static string PaymentMethod(PaymentMethod method) => method switch
    {
        Domain.PaymentMethod.Card => "карта",
        Domain.PaymentMethod.BankTransfer => "банковский перевод",
        Domain.PaymentMethod.Cash => "наличные",
        Domain.PaymentMethod.Other => "другое",
        _ => "не указан"
    };

    public static string PaymentMethodIcon(PaymentMethod method) => method switch
    {
        Domain.PaymentMethod.Card => "💳",
        Domain.PaymentMethod.BankTransfer => "🏦",
        Domain.PaymentMethod.Cash => "💵",
        Domain.PaymentMethod.Other => "💰",
        _ => "💳"
    };
}
