# RadioSipGateway Linux

Отдельная консольная версия радиошлюза для Debian x86-64, основанная на логике проекта `RadioSipGateway_stage28`.

**Текущий статус: SIP-регистрация и тестовая входящая SIP/SDP/RTP-сессия; это не готовый радиошлюз.** Реализованы каркас .NET 8 Console, проверка INI-конфигурации, SIP REGISTER через UDP, Digest-аутентификация через SIPSorcery, повторные попытки после окончательного отказа и обнаружение входящих INVITE. Для изолированной проверки добавлен выключенный по умолчанию `SIP.AutoAnswerForRtpTest`: при включении вызов принимается тестовой send-only медиасессией SIPSorcery. Она не соединена с ALSA и не является двусторонним аудиомостом. Исходящие вызовы, ALSA и CM108 PTT ещё не подключены.

- [Статус переноса](docs/PORTING_STATUS_RU.md)
- [Проверка SIP-регистрации с Yate](docs/SIP_REGISTER_TEST_RU.md)
- [Проверка входящих SIP INVITE](docs/SIP_INVITE_TEST_RU.md)
- [Тестовая SIP/SDP/RTP-сессия](docs/SIP_RTP_TEST_RU.md)
- [Подготовка Debian x86-64](docs/INSTALL_DEBIAN_RU.md)
- [Пример конфигурации](config/radiogateway.example.ini)

## Политика функциональности

- Пилот-тон и его мультиплексирование полностью исключены из Linux-версии.
- Основной планируемый режим PTT — GPIO/HID микросхемы CM108.
- Программный VOX остаётся опциональным через `Transmit.VoxEnabled`.
- Windows-проект не меняется и остаётся отдельным.

## Проверить конфиг

```bash
dotnet run --project src/RadioSipGateway.Linux -- --check-config --config config/radiogateway.example.ini
```

Обычный запуск выполняет SIP-регистрацию и обнаруживает входящие INVITE; при `SIP.AutoAnswerForRtpTest=false` они отклоняются с 503. Опциональный тестовый автоответ проверяет согласование SIP/SDP/RTP на send-only медиасессии, но не подключает ALSA. Исходящие вызовы, двусторонний ALSA-аудиомост и PTT не работают. Не использовать как готовый шлюз.

## Автоматическая сборка

После загрузки изменений в ветку `main` GitHub Actions выполнит сборку .NET 8 и подготовит self-contained артефакт `linux-x64`. Результат можно скачать на вкладке **Actions** в успешном запуске workflow. Успешная сборка не означает, что SIP/RTP, ALSA и PTT уже полноценно реализованы: workflow проверяет только компиляцию и публикацию текущего кода.

## Перенесённая логика

- Портирован потоковый DTMF-декодер из Windows Stage 28; он работает с моно PCM16 и не зависит от NAudio.
- `--self-test-dtmf` проверяет декодирование синтетических тонов без звукового оборудования.
- Декодер пока не подключён к ALSA/SIP аудиотракту: это следующий этап интеграции.
- Пилот-тон не переносится.


## Stage 6: RTP test without music

The test-only incoming-call media source is explicitly set to `Silence`. This validates SIP/SDP/RTP negotiation without sending music or test tones. It does not implement the ALSA audio bridge or CM108 PTT. See `docs/SIP_RTP_TEST_RU.md`.
