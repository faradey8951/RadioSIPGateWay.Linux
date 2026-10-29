# Первый тест SIP-регистрации на Yate

Этот этап проверяет только SIP REGISTER. Вызовы, RTP, ALSA и PTT через CM108 ещё не реализованы. Не подключайте передатчик и не включайте службу `systemd` для постоянной работы.

## 1. Обновить исходники

Если репозиторий уже клонирован:

```bash
cd ~/RadioSIPGateWay.Linux
git pull
```

Если изменения загружены через браузер GitHub, `git pull` также получит последний коммит.

## 2. Создать локальный конфиг

Не редактируйте пример прямо в репозитории и не публикуйте пароль:

```bash
cp config/radiogateway.example.ini config/radiogateway.local.ini
chmod 600 config/radiogateway.local.ini
nano config/radiogateway.local.ini
```

В секции `[SIP]` укажите реальные значения:

```ini
[SIP]
Server = IP_ИЛИ_ИМЯ_YATE
Port = 5060
Username = SIP_ЛОГИН
Password = НАСТОЯЩИЙ_ПАРОЛЬ
LocalPort = 5062
RegistrationRetrySeconds = 10
RegistrationExpirySeconds = 120
```

Не оставляйте `CHANGE_ME`. Убедитесь, что `LocalPort` свободен и не занят другой SIP-программой. `Server` задаётся без `sip:` и без порта, так как порт указывается отдельно.

## 3. Собрать и проверить конфигурацию

```bash
dotnet build src/RadioSipGateway.Linux/RadioSipGateway.Linux.csproj --configuration Release
dotnet run --project src/RadioSipGateway.Linux --configuration Release --no-build -- --check-config --config config/radiogateway.local.ini
```

## 4. Запустить тест регистрации

```bash
dotnet run --project src/RadioSipGateway.Linux --configuration Release --no-build -- --config config/radiogateway.local.ini
```

Оставьте процесс работать и смотрите журнал. Успех подтверждается строкой `SIP: регистрация успешна`. Остановить — `Ctrl+C`.

## 5. Как понимать журнал

- `регистрация успешна` — Yate принял REGISTER.
- `ошибка регистрации: 401 Unauthorized` или сообщение об отклонении аутентификации — проверьте имя пользователя, пароль и параметры учётной записи в Yate. Первый 401 Digest challenge обычно обрабатывается самим SIP-агентом; важен итоговый результат после challenge.
- `403 Forbidden` — сервер распознал запрос, но запрещает регистрацию для этой учётной записи/настройки.
- `повторная регистрация через ...` — после окончательного отказа приложение планирует новую попытку.
- ошибка bind/`Address already in use` — локальный UDP-порт занят; проверьте `LocalPort` и `ss -lunp`.
- тайм-аут без ответа — проверьте IP/порт Yate, UDP-доступность и firewall.

## 6. Если регистрация не проходит

Скопируйте журнал начиная со строки `SIP: UDP transport` и до ошибки. Не отправляйте пароль, полный локальный конфиг или SIP-пакеты с Authorization-заголовком. При необходимости можно параллельно посмотреть журнал Yate.

Пока не проверяйте PTT: код GPIO CM108 и живой аудиотракт на этом этапе ещё не подключены.
