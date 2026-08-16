# Резервное копирование PostgreSQL

PostgreSQL хранит рабочие данные в Docker volume `postgres-data`. Резервную копию следует создавать через `pg_dump`: полученный файл не зависит от внутреннего пути volume и подходит для контролируемого восстановления.

## Создание backup

Из корня проекта на VPS:

```bash
mkdir -p backups
chmod 700 backups
docker compose exec -T postgres \
  pg_dump -U personalassistant -d personalassistant --format=custom \
  > "backups/personalassistant_$(date +%Y-%m-%d_%H-%M).dump"
```

Проверьте, что файл существует и не пуст:

```bash
ls -lh backups
```

Каталог `backups/` и файлы `*.dump` исключены из Git. Архив содержит персональные данные и должен храниться с ограниченным доступом.

## Рекомендуемая политика

- ежедневный backup ночью;
- хранение последних 14 ежедневных копий на VPS;
- еженедельная копия за пределами VPS;
- периодическая проверка восстановления на отдельной тестовой базе.

Копия только на том же VPS не защищает от потери сервера. Автоматическое расписание, ротация и внешнее хранилище запланированы отдельной задачей TD-010.

## Восстановление

Восстановление заменяет объекты текущей базы данными из backup. Перед началом сделайте свежую копию и остановите бота:

```bash
docker compose stop tgbot
docker compose exec -T postgres \
  pg_restore --clean --if-exists --no-owner \
  -U personalassistant -d personalassistant \
  < backups/personalassistant_YYYY-MM-DD_HH-MM.dump
docker compose start tgbot
docker compose logs --tail=100 tgbot
```

После восстановления проверьте `/health`, `/payments`, `/history` и `/stats`. Не используйте `docker compose down -v`: эта команда удаляет volume PostgreSQL.
