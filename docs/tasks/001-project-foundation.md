# Задача 001: Фундамент проекта

## Статус

Завершена.

## Ветка и коммит

- Ветка: `master`
- Коммит: `ac6caec Initial PersonalAssistant foundation`

## Выполнено

- Создан solution `PersonalAssistant` и проекты Domain, Application, Infrastructure, Bot и Tests.
- Добавлены доменная модель, EF Core `DbContext`, PostgreSQL и миграция `InitialCreate`.
- Реализованы `/start` и `/help`.
- Добавлены Dockerfile, Docker Compose и health check PostgreSQL.
- Добавлены базовые unit-тесты расчета дат платежей.

## Проверки

- `dotnet build` — успешно.
- `dotnet test` — успешно.
- `docker compose config` — успешно.

## Следующая задача

Настройка часового пояса пользователя и начало сценариев платежей.
