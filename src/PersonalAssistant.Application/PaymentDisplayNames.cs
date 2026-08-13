using PersonalAssistant.Domain;

namespace PersonalAssistant.Application;

public static class PaymentDisplayNames
{
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
}
