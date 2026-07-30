# stop_graffiti_kurgan_bot

Бот MAX для приема обращений о незаконных граффити и запрещенных надписях
в Курганской области.

## Что уже работает

- пошаговая подача обращения: категория, фото/видео, адрес, комментарий;
- прием адреса текстом или геопозицией;
- подтверждение и отмена;
- присвоение номера обращению;
- хранение заявок в `data/reports.jsonl`;
- уведомление служебного чата;
- long polling для локальной разработки;
- защищенный webhook для production.

## Локальный запуск

Нужен .NET 8 и токен бота MAX.

```bash
cd ClassLibrary1
Bot__Token='ТОКЕН_БОТА' \
Bot__UseLongPolling=true \
dotnet run
```

Команды пользователя: `/start`, `/new`, `/cancel`.

Long polling работает, только если у бота нет активной webhook-подписки.

## Docker

Для проверки без публичного домена:

```bash
cp .env.example .env
# Заполнить BOT_TOKEN в .env
docker compose -f compose.dev.yaml up --build
```

Для production используется `compose.yaml`: приложение подключается к общей
Docker-сети `vi-common_vi`, а существующий Traefik выпускает TLS-сертификат и
принимает webhook на порту 443.
Полная инструкция: [docs/deployment.md](docs/deployment.md).

## Настройки

Настройки задаются переменными окружения с двойным подчеркиванием:

| Переменная | Назначение |
| --- | --- |
| `Bot__Token` | Токен бота MAX |
| `Bot__UseLongPolling` | `true` для локальной разработки |
| `Bot__ReviewerChatId` | ID служебного чата, куда приходят новые заявки |
| `Bot__WebhookSecret` | Секрет проверки заголовка webhook |
| `Bot__DataDirectory` | Каталог хранения заявок |
| `Bot__VikaApiBaseUrl` | Базовый URL «Вики» для передачи обращений |
| `Bot__VikaApiToken` | Общий секрет интеграции с «Викой» |
| `Bot__VikaCallbackToken` | Секрет обратных уведомлений о статусах |

## Production

1. Разместить приложение за HTTPS на домене с доверенным сертификатом.
2. Установить `Bot__UseLongPolling=false`.
3. Задать случайный `Bot__WebhookSecret` длиной от 5 символов.
4. Подписать MAX на `https://ВАШ-ДОМЕН/webhook`:

```bash
curl -X POST 'https://platform-api2.max.ru/subscriptions' \
  -H 'Authorization: ТОКЕН_БОТА' \
  -H 'Content-Type: application/json' \
  -d '{
    "url": "https://ВАШ-ДОМЕН/webhook",
    "update_types": ["message_created", "message_callback", "bot_started"],
    "secret": "ВАШ_WEBHOOK_SECRET"
  }'
```

MAX требует HTTPS webhook на порту 443. Токен и секрет нельзя добавлять в
`appsettings.json` или публиковать в репозитории.

## Следующий этап перед публичным запуском

Файловое хранилище подходит для пилота на одном сервере. Перед полноценным
запуском следует подключить PostgreSQL, операторский интерфейс со статусами,
резервное копирование, политику обработки персональных данных и сроки хранения
материалов.
