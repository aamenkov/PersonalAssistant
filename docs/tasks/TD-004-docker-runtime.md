# Текущая задача

## ID задачи

TD-004

## Этап

Исправление Docker runtime для запуска контейнера.

## Ветка

`codex/TD-004-docker-runtime`

## Цель

Сделать так, чтобы опубликованный Docker-образ содержал shared framework, необходимый приложению TgBot.

## Задачи этапа

- [x] Проверить причину ошибки запуска на VPS.
- [x] Заменить неподходящий .NET runtime-образ.
- [x] Проверить сборку solution.
- [x] Обновить документацию и архив задачи.

## Критерии готовности

- Финальный Docker stage использует образ с `Microsoft.AspNetCore.App` 8.
- Сборка проекта проходит.
- Перед Pull Request файл перемещен в `docs/tasks/TD-004-docker-runtime.md`.

## Результат и проверки

- Причина ошибки подтверждена по логам контейнера: использовался `dotnet/runtime`, не содержащий ASP.NET Core shared framework.
- Финальный образ заменён на `mcr.microsoft.com/dotnet/aspnet:8.0`.
- `dotnet build PersonalAssistant.sln --no-restore` — успешно.
- `dotnet test PersonalAssistant.sln --no-restore` — 34 unit-теста успешно.
- `docker compose config` и `git diff --check` — успешно.
