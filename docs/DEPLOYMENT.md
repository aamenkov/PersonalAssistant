# Установка и запуск PersonalAssistant

Инструкция для первого запуска на локальной машине или VPS. Все команды выполняются из корня репозитория.

## Что понадобится

- VPS на Ubuntu/Debian с доступом по SSH;
- Git;
- Docker Engine и Docker Compose plugin;
- токен Telegram-бота от [@BotFather](https://t.me/BotFather);
- Telegram User ID пользователя, которому будет разрешен доступ.

На чистом Debian/Ubuntu установите базовые пакеты:

```bash
sudo apt update
sudo apt install -y git docker.io docker-compose-plugin
sudo systemctl enable --now docker
sudo usermod -aG docker "$USER"
```

После добавления пользователя в группу Docker переподключитесь по SSH. Проверьте установку:

```bash
docker --version
docker compose version
```

Если пакет `docker-compose-plugin` недоступен в репозитории вашей ОС, установите Docker Engine и Compose plugin по официальной инструкции Docker.

## Создание Telegram-бота

1. Откройте [@BotFather](https://t.me/BotFather).
2. Выполните `/newbot` и задайте имя и username.
3. Сохраните токен только в менеджере секретов или в локальном `.env`. Не добавляйте его в Git, сообщения об ошибках и скриншоты.

Узнать свой Telegram User ID можно через надежный бот-определитель ID. Это не Chat ID группы и не username.

## Установка на чистый VPS

```bash
cd ~
git clone https://github.com/aamenkov/PersonalAssistant.git
cd ~/PersonalAssistant
git switch master
```

Создайте файл `.env`:

```bash
nano .env
```

Укажите реальные значения только на сервере:

```dotenv
TELEGRAM_BOT_TOKEN=токен_бота
TELEGRAM_ALLOWED_USER_IDS=твой_telegram_id
# необязательно: Telegram User ID администратора
TELEGRAM_ADMIN_USER_ID=твой_telegram_id
POSTGRES_PASSWORD=надежный_пароль
```

`TELEGRAM_ADMIN_USER_ID` необязателен. Если он не задан, администратором считается единственный пользователь из `TELEGRAM_ALLOWED_USER_IDS`. Если список разрешенных пользователей пуст, админская панель не показывается автоматически. Администратор видит кнопку «Админская панель»: очистка истории удаляет только оплаты, а полное удаление платежей удаляет расписания и их историю без возможности восстановления, поэтому оба действия требуют подтверждения.

Ограничьте права файла с секретами и запустите проект:

```bash
chmod 600 .env
docker compose up -d --build
docker compose ps
docker compose logs -f tgbot
```

При первом запуске приложение дождется PostgreSQL, автоматически применит EF Core миграции и начнет Telegram polling. В Telegram выполните `/start`: бот зарегистрирует пользователя и предложит выбрать часовой пояс.

Для первого smoke-теста:

1. Проверьте в логах сообщения `Database migrations applied` и `PersonalAssistant polling started`.
2. Проверьте health endpoint: `curl http://localhost:8080/health` должен вернуть JSON со статусом `ok`.
3. Выполните `/start` и выберите часовой пояс. Для российского региона можно нажать «Указать текущее время», ввести местное время `ЧЧ:ММ` и подтвердить найденное смещение.
4. Создайте тестовый платеж через `/add`.
5. Проверьте `/payments`, `/upcoming`, `/edit` и `/pay`.
6. Проверьте `/history` и `/stats`.
7. Временно настройте время напоминания в `/settings` и проверьте уведомление с кнопкой «Оплатил».

После успешной проверки убедитесь, что `TELEGRAM_ALLOWED_USER_IDS` содержит только нужные Telegram User ID.

## Обновление проекта

Перед обновлением убедитесь, что изменения уже находятся в `master`:

```bash
cd ~/PersonalAssistant
git switch master
git pull --ff-only origin master
```

Пересоберите приложение и запустите его снова:

```bash
docker compose build --no-cache tgbot
docker compose up -d
docker compose logs -f tgbot
```

Миграции базы применяются автоматически при старте приложения. Не используйте `docker compose down -v` при обычном обновлении: команда удаляет volume PostgreSQL вместе с данными.

## Управление и диагностика

```bash
# остановить контейнеры без удаления данных
docker compose down

# запустить уже собранную версию
docker compose up -d

# посмотреть статус и логи
docker compose ps
docker compose logs --tail=100 tgbot
docker compose logs -f tgbot

# проверить PostgreSQL
docker compose exec postgres pg_isready -U personalassistant -d personalassistant
```

Если бот не запускается, проверьте `.env`, значение `TELEGRAM_BOT_TOKEN`, доступность Docker и последние строки логов. Токен не вставляйте в сообщения при обращении за помощью.

## Резервное копирование

Данные хранятся в Docker volume `postgres-data`, но копии создаются через `pg_dump`. Команды создания и восстановления, рекомендуемая ротация и правила хранения описаны в [BACKUP.md](BACKUP.md).

Перед публикацией MVP 2 проверьте, что задан надежный `POSTGRES_PASSWORD`, `TELEGRAM_ALLOWED_USER_IDS` ограничивает доступ, `/health` отвечает только через `localhost:8080`, а свежий backup сохранен за пределами VPS.

## Локальный запуск

Для локального запуска используйте `.env` и Docker Compose:

```powershell
Copy-Item .env.example .env
# заполните .env безопасными локальными значениями
docker compose up --build
```

Альтернативно проект можно запустить через .NET SDK и User Secrets, если PostgreSQL уже доступен локально. Команды проверок находятся в README.
