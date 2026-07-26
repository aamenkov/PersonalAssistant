# PersonalAssistant

PersonalAssistant — расширяемый Telegram-бот, первым модулем которого становится учет регулярных и обязательных платежей.

Бот поддерживает создание, редактирование и отключение платежей, запись оплат, историю и месячную статистику по валютам. Проект подготовлен к ограниченному запуску для одного пользователя.

## Технологии

- C# и .NET 8;
- ASP.NET Core Generic Host;
- Telegram Bot API;
- Entity Framework Core и PostgreSQL;
- Docker Compose;
- встроенный Dependency Injection и `ILogger`.

## Структура solution

```text
src/
  TgBot             # запуск, Telegram handlers, конфигурация
  PersonalAssistant.Application     # use cases, DTO и интерфейсы
  PersonalAssistant.Domain          # сущности и бизнес-правила
  PersonalAssistant.Infrastructure  # EF Core, PostgreSQL, Telegram и фоновые задачи
tests/
  PersonalAssistant.UnitTests
  PersonalAssistant.IntegrationTests
```

Подробности находятся в [ARCHITECTURE.md](ARCHITECTURE.md), требования — в [REQUIREMENTS.md](REQUIREMENTS.md), шаблон активной задачи — в [TASK.template.md](TASK.template.md). Активный `TASK.md` создается только внутри рабочей ветки.

## Создание Telegram-бота

1. Откройте официальный бот [@BotFather](https://t.me/BotFather).
2. Выполните `/newbot`, задайте имя и username.
3. Сохраните полученный токен локально. Не добавляйте его в Git, сообщения об ошибках или скриншоты.

## Конфигурация

Секреты не хранятся в Git. Приложение завершает запуск с понятной ошибкой, если токен или строка подключения пусты.

Основные параметры:

- `Telegram__BotToken` — токен BotFather для локального запуска;
- `ConnectionStrings__Postgres` — строка подключения для локального запуска;
- `TELEGRAM_BOT_TOKEN` — токен для Docker Compose;
- `POSTGRES_PASSWORD` — пароль PostgreSQL для Docker Compose;
- `TELEGRAM_ALLOWED_USER_ID` — необязательный Telegram User ID единственного разрешенного пользователя.

Если `TELEGRAM_ALLOWED_USER_ID` пуст, бот принимает пользователей согласно обычной регистрации. После первого `/start` ID можно посмотреть в PostgreSQL и затем включить ограничение:

```bash
docker compose exec postgres psql -U personalassistant -d personalassistant -c 'SELECT "TelegramUserId", "Username" FROM "Users";'
```

## Запуск через Docker

Создайте локальный `.env` из безопасного шаблона и заполните значения:

```powershell
Copy-Item .env.example .env
docker compose up --build
```

Файл `.env` исключен из Git. После готовности PostgreSQL приложение автоматически применяет EF Core миграции и запускает Telegram polling. Логи можно посмотреть командой:

```bash
docker compose logs -f tgbot
```

Для первого smoke-теста:

1. Убедитесь, что в логах есть `Database migrations applied` и `PersonalAssistant polling started`.
2. Выполните `/start` и выберите часовой пояс.
3. Создайте тестовый платеж через `/add`.
4. Проверьте `/payments`, `/upcoming`, `/edit` и `/pay`.
5. Проверьте `/history` и `/stats`.
6. После успешного теста задайте `TELEGRAM_ALLOWED_USER_ID` и перезапустите контейнер.

Остановка без удаления данных:

```bash
docker compose down
```

Volume PostgreSQL сохраняется. Команда `docker compose down -v` удаляет локальную базу и должна использоваться только осознанно.

## Локальный запуск

Настройте User Secrets:

```bash
dotnet user-secrets set "Telegram:BotToken" "replace-with-botfather-token" --project src/TgBot
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=personalassistant;Username=personalassistant;Password=replace-me" --project src/TgBot
```

Запустите PostgreSQL, затем:

```bash
dotnet run --project src/TgBot
```

Миграции применяются автоматически. Для ручной проверки или разработки миграций:

```powershell
$env:ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=personalassistant;Username=personalassistant;Password=replace-me"
dotnet ef database update --project src/PersonalAssistant.Infrastructure --startup-project src/PersonalAssistant.Infrastructure
```

## Проверки

```bash
dotnet build PersonalAssistant.sln
dotnet test PersonalAssistant.sln
docker compose config
```

## Документация

- [Шаблон текущей задачи](TASK.template.md)
- [Требования](REQUIREMENTS.md)
- [Архитектура](ARCHITECTURE.md)
- [Будущие задачи](TODO.md)
- [Инструкции для AI-агентов](AGENTS.md)
- [Архив завершенных задач](docs/tasks/001-project-foundation.md)
- [Задача 002: часовой пояс](docs/tasks/002-timezone-onboarding.md)
- [Задача 003: переименование проекта](docs/tasks/003-personal-assistant-naming.md)
- [Задача 004: архив и Git workflow](docs/tasks/004-task-archive-workflow.md)
- [Задача 005: создание и просмотр платежей](docs/tasks/005-payment-creation-and-listing.md)
- [Задача 006: редактирование и отключение](docs/tasks/006-payment-editing.md)
- [Задача 007: оплата и история](docs/tasks/007-payment-history.md)
- [Задача 008: статистика и история за период](docs/tasks/008-statistics-history.md)
- [Задача 009: аудит перед запуском](docs/tasks/009-prelaunch-audit.md)

## Команды бота

- `/start` — регистрация и выбор часового пояса;
- `/settings` — повторный выбор часового пояса;
- `/add` — последовательное добавление платежа;
- `/payments` — список активных платежей;
- `/upcoming` — платежи на ближайшие 7 дней;
- `/edit` — изменить параметры платежа;
- `/disable` — отключить платеж без удаления истории;
- `/pay` — отметить платеж оплаченным;
- `/history [YYYY-MM]` — история текущего или выбранного месяца;
- `/stats [YYYY-MM]` — статистика текущего или выбранного месяца;
- `/help` — справка.

## Ограничения текущего запуска

- напоминания еще не отправляются автоматически;
- интеграционные тесты PostgreSQL будут добавлены отдельной задачей;
- исходный день 29–31 после перехода в короткий месяц пока не восстанавливается автоматически;
- запуск рассчитан на один экземпляр приложения.

Текущую версию можно запускать для одного пользователя с ручным учетом платежей. Для ограничения доступа задайте `TELEGRAM_ALLOWED_USER_ID`.
