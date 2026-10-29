# Stage 5 — безопасный тест SIP/SDP/RTP

Этот этап проверяет приём входящего INVITE, ответ 180 Ringing/200 OK и согласование SDP/RTP через тестовую медиасессию SIPSorcery.

**Ограничения:** тестовая медиасессия send-only. Она не подключает ALSA, не передаёт звук с CM108 в SIP и не воспроизводит принятый RTP в радиоканал. PTT/GPIO не используется. Не подключайте передатчик для этого теста.

## 1. Включить только для теста

В `config/radiogateway.local.ini` в секции `[SIP]` добавьте:

```ini
AutoAnswerForRtpTest = true
```

Проверьте, что это локальный файл и в нём правильные параметры SIP. Не публикуйте пароль.

## 2. Обновить и собрать

```bash
git pull
dotnet build src/RadioSipGateway.Linux/RadioSipGateway.Linux.csproj --configuration Release
```

## 3. Запустить

```bash
dotnet run --project src/RadioSipGateway.Linux --configuration Release --no-build -- --config config/radiogateway.local.ini
```

Позвоните на зарегистрированный SIP-аккаунт. Ожидаемые сообщения:

- `получен INVITE` с From, Request-URI и To;
- `отправлен 180 Ringing`;
- после согласования — `тестовый вызов ... принят; SIP/SDP/RTP сессия поднята`.

Завершите вызов со стороны телефона и убедитесь, что приложение пишет о завершении вызова. Затем остановите приложение Ctrl+C.

## 4. После теста

Верните `AutoAnswerForRtpTest = false`. Это тестовая опция, не режим эксплуатации. Если сборка падает, пришлите полный вывод `dotnet build`. Если INVITE приходит, но RTP-сессия не устанавливается, пришлите журнал приложения и соответствующий SIP/Yate журнал, скрыв пароли.
