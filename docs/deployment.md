# Развертывание на сервере

## Требования

- сервер Linux с публичным IP;
- домен с A-записью на IP сервера;
- открытые входящие TCP-порты 80 и 443;
- Docker Engine с плагином Docker Compose;
- токен бота MAX.

## Первый запуск

```bash
git clone https://github.com/Spaceonmymind/stop_grafity_kurgan.git
cd stop_grafity_kurgan
cp .env.example .env
nano .env
docker compose up -d --build
docker compose ps
docker compose logs -f bot
```

В `.env` обязательно заполнить:

- `BOT_TOKEN` — токен чат-бота MAX;
- `WEBHOOK_SECRET` — случайная строка длиной 32 и более символов;
- `DOMAIN` — домен без `https://`;
- `ACME_EMAIL` — адрес для уведомлений о TLS-сертификате;
- `REVIEWER_CHAT_ID` — ID служебного чата, если уведомления нужны сразу.

Caddy автоматически запросит сертификат после того, как DNS начнет указывать
на сервер. Проверка:

```bash
curl https://ВАШ-ДОМЕН/health
```

Ожидаемый ответ: `{"status":"healthy"}`.

## Регистрация webhook в MAX

После успешной проверки HTTPS:

```bash
set -a
. ./.env
set +a

curl -X POST 'https://platform-api2.max.ru/subscriptions' \
  -H "Authorization: ${BOT_TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{
    \"url\": \"https://${DOMAIN}/webhook\",
    \"update_types\": [\"message_created\", \"message_callback\", \"bot_started\"],
    \"secret\": \"${WEBHOOK_SECRET}\"
  }"
```

Список активных подписок:

```bash
curl 'https://platform-api2.max.ru/subscriptions' \
  -H "Authorization: ${BOT_TOKEN}"
```

## Обновление

```bash
git pull --ff-only
docker compose up -d --build
docker image prune -f
```

## Резервная копия заявок

Данные находятся в именованном томе `bot_data`. Создать архив:

```bash
docker run --rm \
  -v stop_grafity_kurgan_bot_data:/data:ro \
  -v "$PWD":/backup \
  alpine \
  tar czf /backup/bot-data-backup.tar.gz -C /data .
```

Имя тома можно уточнить командой `docker volume ls`.

## Локальная проверка через Docker

Когда публичного домена еще нет, используйте long polling:

```bash
cp .env.example .env
# Для этого режима достаточно заполнить BOT_TOKEN.
docker compose -f compose.dev.yaml up --build
```

У бота при этом не должно быть активной webhook-подписки.
