# RadioSipGateway Linux

Отдельная консольная версия радиошлюза для Debian x86-64, основанная на логике проекта `RadioSipGateway_stage28`.

**Текущий статус: экспериментальные SIP/SDP/RTP, ALSA/RTP и CM108 PTT/COS этапы; это ещё не готовый радиошлюз.** Реализованы SIP REGISTER/обнаружение INVITE, тестовый auto-answer, двусторонний ALSA/RTP мост G.711 и опциональное управление GPIO3 CM108 с COS на VOLDN. Сборка и полноценный реальный аудиотест/PTT-тест должны быть подтверждены на целевом Debian. PTT выключен по умолчанию и требует явного `Transmit.PttEnabled=true`. Исходящие вызовы и полная радио-логика Windows-версии ещё не портированы.

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

- Stage 7 (experimental): двусторонний ALSA/RTP тест — `docs/ALSA_RTP_BRIDGE_STAGE7_RU.md`.
- Stage 8 (experimental): CM108 GPIO3 PTT + COS через VOLDN — `docs/PTT_COS_STAGE8_RU.md`. PTT выключен по умолчанию.


## CM108 PTT/COS (experimental)

`Transmit.PttActiveLevel = High|Low` выбирает активный уровень GPIO3, `Transmit.CosActiveHigh` независимо задаёт полярность COS/VOLDN. При `VoxEnabled=false` любой входящий RTP-пакет, в том числе тишина, считается активностью для PTT. Подробности: `docs/PTT_COS_STAGE8_FIX1_RU.md`.
