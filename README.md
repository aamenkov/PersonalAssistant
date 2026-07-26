# PersonalAssistant

PersonalAssistant — расширяемый Telegram-бот, первым модулем которого становится учет регулярных и обязательных платежей.

Фундамент MVP завершен: solution, доменная модель, PostgreSQL/EF Core, регистрация пользователя через `/start`, выбор часового пояса, справка через `/help`, Docker Compose, миграция и базовые unit-тесты.

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

## Конфигурация

Секреты не хранятся в Git. Для запуска приложения используются переменные окружения `Telegram__BotToken` и `ConnectionStrings__Postgres` либо локальные User Secrets.

## Запуск

После завершения фундаментального этапа:

```bash
dotnet build
dotnet test
docker compose up --build
```

Инструкции по BotFather, миграциям и полноценному запуску будут дополнены по мере реализации соответствующих компонентов.

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

## Команды бота

- `/start` — регистрация и выбор часового пояса;
- `/settings` — повторный выбор часового пояса;
- `/help` — справка.

## Статус

Этап 1 завершен. Следующий этап — добавление сценариев создания и просмотра платежей.
