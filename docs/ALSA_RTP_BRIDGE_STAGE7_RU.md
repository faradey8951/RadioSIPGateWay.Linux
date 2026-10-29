# Stage 7 — первый двусторонний ALSA/RTP тест (без PTT)

Этот этап добавляет тестовый аудиомост для Linux через системные `arecord` и `aplay`:

- CM108 capture → PCM mono S16_LE 8000 Hz → SIPSorcery → RTP/SIP;
- RTP/SIP → G.711 PCMU/PCMA decode → PCM mono S16_LE 8000 Hz → CM108 playback.

Управление CM108 PTT/GPIO **не реализовано и не вызывается**. Не подключайте радиопередатчик к этому тесту. Это первый этап интеграции, который нужно собрать и проверить на реальном Debian/CM108; он ещё не является готовым эксплуатационным шлюзом.

## 1. Установить ALSA utilities

```bash
sudo apt update
sudo apt install -y alsa-utils
```

Проверьте устройства:

```bash
arecord -l
aplay -l
arecord -L | head -60
aplay -L | head -60
```

В данном тестовом стенде ранее использовалось `plughw:1,0`; на другом компьютере номер карты может отличаться.

## 2. Собрать

```bash
git pull
dotnet build src/RadioSipGateway.Linux/RadioSipGateway.Linux.csproj --configuration Release
```

Если сборка выдаёт ошибки, не запускайте приложение — пришлите полный вывод сборки.

## 3. Включить мост только в локальном конфиге

В `config/radiogateway.local.ini`:

```ini
[SIP]
AutoAnswerForRtpTest = true

[Audio]
CaptureDevice = plughw:1,0
PlaybackDevice = plughw:1,0
BridgeEnabled = true
```

Оставьте `Transmit.Mode = cm108`, но не добавляйте путь HID и не подключайте PTT для этого теста. Параметр `BridgeEnabled` не меняет управление передатчиком: оно остаётся отключённым в программе.

Не публикуйте локальный конфиг с паролем.

## 4. Запустить и проверить оба направления

```bash
dotnet run --project src/RadioSipGateway.Linux --configuration Release --no-build -- --config config/radiogateway.local.ini
```

1. Позвоните на SIP-аккаунт шлюза.
2. Сначала проверьте, что голос с телефона слышен на аудиовыходе CM108 (подключите наушники/линейный монитор, не передатчик).
3. Затем подайте негромкий тестовый звук на микрофонный вход CM108 и проверьте, слышен ли он на телефоне.
4. Завершите вызов, проверьте, что процессы ALSA завершаются и приложение остаётся живым.

Если один канал не работает, пришлите полный журнал вызова, а также вывод `arecord -l`, `aplay -l` и проверок `arecord`/`aplay` для устройства. Не подключайте PTT, пока оба направления не проверены.

## Ограничения этого этапа

- Передаётся только G.711 PCMU/PCMA, 8 kHz mono.
- Аудио берётся/проигрывается через внешние процессы `arecord`/`aplay`.
- Нет PTT, VOX, TOT, DTMF-маршрутизации, сигналов courtesy/ringback и радио-сценариев.
- Пока это тестовый ALSA/RTP-мост, а не полноценный перенос Windows-шлюза.
