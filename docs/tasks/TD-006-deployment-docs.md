# TD-006 — Инструкции запуска и эксплуатации

## Выполнено

- создан отдельный документ `docs/DEPLOYMENT.md`;
- из README вынесены создание Telegram-бота, конфигурация, первый запуск, обновление и диагностика;
- добавлены инструкции установки Docker на чистый VPS;
- добавлены smoke-тест, резервное копирование и предупреждение о сохранности PostgreSQL volume;
- из README удалены ссылки на отдельные номера архивных задач;
- проверены сборка, тесты, `docker compose config` и форматирование diff.

## Проверка

- `dotnet build PersonalAssistant.sln --no-restore` — успешно;
- `dotnet test PersonalAssistant.sln --no-restore` — 57 unit-тестов и 1 integration test успешно;
- `docker compose config` — успешно;
- `git diff --check` — успешно.
