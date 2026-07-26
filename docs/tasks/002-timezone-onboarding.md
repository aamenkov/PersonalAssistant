# Задача 002: Часовой пояс и Git workflow

## Статус

Завершена и включена в `master`.

## Ветка и коммиты

- Ветка: `codex/002-timezone-onboarding`
- Коммит: `e2f6524 Add timezone onboarding and Git workflow`

## Выполнено

- Добавлен выбор часового пояса при `/start`.
- Добавлена команда `/settings` для повторного выбора.
- Часовой пояс валидируется через `TimeZoneInfo` и сохраняется в профиле пользователя.
- Добавлена миграция `AddUserTimeZoneConfiguration`.
- Добавлены тесты профиля пользователя.
- Зафиксирован Git workflow с ветками и архивом задач.

## Проверки

- `dotnet build TelegramAssistant.sln` — успешно.
- `dotnet test TelegramAssistant.sln` — успешно.
- `docker compose config` — успешно.
