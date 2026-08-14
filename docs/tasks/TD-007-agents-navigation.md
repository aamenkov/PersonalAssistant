# TD-007 — Навигация и правила работы агента

## Выполнено

- `AGENTS.md` дополнен разделом Source of Truth с приоритетом актуальных требований;
- добавлена компактная карта Domain, Application, Infrastructure и Telegram/Bot;
- добавлены Navigation Hints для платежей, дат, статистики, напоминаний, UI и БД;
- явно описаны исторический статус `docs/tasks/` и правило не восстанавливать старое поведение из архива;
- добавлены Repository Exploration, Development Commands и Definition of Done;
- уточнены архитектурные границы и task/Git workflow.

## Проверка

- Code Map сверена с реальными путями текущего репозитория;
- `dotnet build PersonalAssistant.sln --no-restore` — успешно;
- `dotnet test PersonalAssistant.sln --no-restore` — 57 unit-тестов и 1 integration test успешно;
- `git diff --check` — успешно.

`ARCHITECTURE.md` не изменялся: найденные описания соответствуют текущей реализации, а подробности намеренно оставлены в архитектурном документе.
