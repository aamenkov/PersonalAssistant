# AGENTS.md

## Project

PersonalAssistant — .NET 8 Telegram-бот. Первый модуль — учет регулярных платежей.

Solution: `PersonalAssistant.sln`.
Основные проекты: `PersonalAssistant.Domain`, `PersonalAssistant.Application`, `PersonalAssistant.Infrastructure`, `TgBot`.
Тесты: `PersonalAssistant.UnitTests`, `PersonalAssistant.IntegrationTests`.

## Source of Truth

При конфликте требований использовать следующий порядок:

1. Текущий `TASK.md`.
2. `REQUIREMENTS.md`.
3. Актуальные доменные правила и тематические документы в `docs/`.
4. `ARCHITECTURE.md`.
5. Текущая реализация и тесты.
6. Исторический архив `docs/tasks/`.

`docs/tasks/` — архив завершенных задач. Старые файлы можно использовать для понимания причин решений, но не как актуальное ТЗ. Не восстанавливать старое поведение только потому, что оно описано в архиве.

## Before Starting Work

Перед изменениями:

1. Проверить ветку и чистоту рабочего дерева.
2. Прочитать `TASK.md`, `REQUIREMENTS.md` и `ARCHITECTURE.md`. Если активного `TASK.md` нет, создать отдельную задачу по `TASK.template.md`.
3. При необходимости сверить `README.md`, `TODO.md` и относящиеся файлы в `docs/tasks/`.
4. Определить реальные пути к коду, тестам и конфигурации; не предполагать структуру по названию задачи.

Каждая самостоятельная задача получает постоянный ID и отдельную ветку `codex/<ID>-short-name`. Новую задачу начинать от актуального принятого `master`.

## Code Map

Карта основных точек входа:

### Domain

- `src/PersonalAssistant.Domain/Entities.cs` — сущности пользователя, платежа, транзакции, напоминания и состояния диалогов.
- `src/PersonalAssistant.Domain/Enums.cs` — доменные перечисления.
- `src/PersonalAssistant.Domain/PaymentDateCalculator.cs` — расчет следующей даты платежа.

### Application

- `src/PersonalAssistant.Application/PaymentServices.cs` — операции с платежами, статистика и сценарии создания, редактирования и записи оплаты.
- `PaymentService.GetMonthlyStatisticsAsync` в `PaymentServices.cs` — месячная статистика.
- `src/PersonalAssistant.Application/ReminderServices.cs` — расчет напоминаний и защита от повторной заявки.
- `src/PersonalAssistant.Application/UserRegistrationService.cs` — регистрация и настройки пользователя.
- `src/PersonalAssistant.Application/DateShortcutCalculator.cs` — быстрые варианты дат.

### Infrastructure

- `src/PersonalAssistant.Infrastructure/PersonalAssistantDbContext.cs` — EF Core DbContext и реализации репозиториев.
- `src/PersonalAssistant.Infrastructure/Migrations/` — миграции PostgreSQL.
- `src/PersonalAssistant.Infrastructure/DependencyInjection.cs` — регистрация инфраструктурных зависимостей.

### Telegram / Bot

- `src/TgBot/Program.cs` — composition root, DI, polling и маршрутизация Update.
- `src/TgBot/TelegramUi.cs` — пользовательские тексты, клавиатуры и форматирование экранов.
- `src/TgBot/TelegramCallbackData.cs` — callback-префиксы и значения.
- `src/TgBot/TelegramPresentation.cs` — форматирование денег, дат и относительных сроков.
- `src/TgBot/ReminderBackgroundService.cs` — фоновая отправка напоминаний.
- `src/TgBot/BotAccessPolicy.cs` — ограничение доступа по Telegram User ID.

## Navigation Hints

Если задача касается создания или редактирования платежей:

1. Начать с соответствующего сценария в `PaymentServices.cs`.
2. Затем проверить `PaymentService`.
3. Перейти в Domain и Infrastructure только если затрагиваются правила или хранение.

Если задача касается записи оплаты или ошибки повторной оплаты, смотреть `PaymentRecordConversationService`, `PaymentService.RecordPaymentAsync`, уникальность транзакции и тесты `PaymentRecordTests.cs`/интеграционные тесты.

Если задача касается расчета следующей даты, начинать с `PaymentDateCalculator.cs` и его тестов. Не помещать recurrence logic в Bot или Infrastructure.

Если задача касается текста, кнопок или callback, начинать с `TelegramUi.cs`, `TelegramCallbackData.cs` и `TelegramPresentation.cs`; не начинать с `Program.cs`, если проблема не связана с host, DI или маршрутизацией.

Если задача касается статистики, начать с `PaymentService.GetMonthlyStatisticsAsync` и `MonthlyStatisticsTests.cs`.

Если задача касается напоминаний, проверить `ReminderServices.cs`, `ReminderBackgroundService.cs`, `ReminderRepository`, уникальный ключ occurrence и тесты идемпотентности.

Если задача касается БД, сначала найти интерфейс репозитория в Application, затем реализацию в Infrastructure, миграции и интеграционные тесты.

Подробнее о взаимодействии слоев см. `ARCHITECTURE.md`; общие переносимые правила Telegram UX см. `docs/telegram-ux-guidelines.md`. Не дублировать эти документы в `AGENTS.md`.

## Architecture Rules

- Не смешивать Telegram, EF Core и бизнес-правила в одном обработчике.
- Domain не зависит от Telegram, EF Core или PostgreSQL.
- Application содержит сценарии, DTO, валидацию и интерфейсы хранилищ.
- Infrastructure содержит EF Core, PostgreSQL, миграции и фоновые адаптеры.
- `TgBot` содержит host, DI и Telegram-адаптер.
- Не добавлять библиотеки, паттерны или новые abstraction без реальной необходимости.
- Не помещать секреты в исходный код, конфигурацию, Git или логи.
- Все операции с платежами должны проверять владельца Telegram-пользователя.

## Repository Exploration

Не сканировать весь репозиторий без необходимости. Начинать с файлов и модулей, непосредственно связанных с текущей задачей, и расширять исследование только по зависимостям или бизнес-правилам.

Перед созданием нового сервиса, helper или abstraction проверить, нет ли уже компонента с такой ответственностью. Не создавать дубли вроде `PaymentService2`, `NewPaymentScheduler` или `AnotherReminderService`.

## Task Workflow

- `TODO.md` содержит только незавершенные задачи: бизнесовые ID — `NNN`, технический долг — `TD-NNN`.
- Выполненную задачу удалить из `TODO.md`, а итог и проверки сохранить в `docs/tasks/<ID>-short-name.md`.
- Не переиспользовать и не перенумеровывать ID.
- Перед Pull Request завершенный `TASK.md` переместить в архив; в корне не должно быть активного `TASK.md`.
- Найденную вне текущего scope проблему добавить отдельным пунктом `TD-NNN` в `TODO.md`, а не терять или маскировать ее.
- Не создавать следующую задачу в той же ветке после архивации текущей.

## Git Workflow

Основная ветка — `master`. Рабочий цикл:

```powershell
git switch master
git pull --ff-only origin master
git switch -c codex/<ID>-short-name
Copy-Item TASK.template.md TASK.md
# заполнить TASK.md
```

После проверок:

```powershell
git add .
git commit -m "Short task description"
git push -u origin codex/<ID>-short-name
```

Pull Request предлагать только после публикации ветки, проверки архива задачи и чистого `git status`. Секреты, `.env` и реальные токены в Git не добавлять.

## Development Commands

Из корня репозитория:

```powershell
dotnet build PersonalAssistant.sln
dotnet test PersonalAssistant.sln
$env:TELEGRAM_BOT_TOKEN='test-token'
docker compose config
dotnet run --project src/TgBot
```

Для запуска PostgreSQL и приложения через Docker:

```powershell
docker compose up -d --build
docker compose logs -f tgbot
```

Подробная установка на VPS и обновление описаны в `docs/DEPLOYMENT.md`.

## Definition of Done

Задача завершена, когда:

- выполнены требования текущего `TASK.md`;
- добавлены или обновлены относящиеся тесты;
- проходят `dotnet build`, `dotnet test` и необходимые проверки;
- обновлены документация и `TODO.md`, если это требуется;
- `TASK.md` перемещен в `docs/tasks/`, а в корне его нет;
- изменения сохранены коммитом и опубликованы в отдельной ветке;
- `git status` чистый после коммита.
