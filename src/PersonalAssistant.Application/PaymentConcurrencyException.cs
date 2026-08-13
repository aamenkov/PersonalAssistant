namespace PersonalAssistant.Application;

public sealed class PaymentConcurrencyException : Exception
{
    public PaymentConcurrencyException(Exception innerException)
        : base("Payment changed while it was being saved.", innerException) { }
}
