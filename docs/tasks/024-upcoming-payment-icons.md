# Задача 024. Иконки способов оплаты в ближайших платежах

В карточках «Ближайшие платежи» теперь используется способ оплаты из `UpcomingPaymentItem` и общий `PaymentDisplayNames.PaymentMethodIcon`, как и в карточках «Все платежи».

Для просроченных платежей сохраняется приоритетная иконка `⚠️`, чтобы явно обозначить просрочку.

Проверки:

- `dotnet build PersonalAssistant.sln --no-restore` — успешно;
- `dotnet test PersonalAssistant.sln --no-restore` — 72 теста успешно;
- `git diff --check` — успешно.
