# RadioSipGateway Linux

Отдельная консольная версия радиошлюза для Debian x86-64, основанная на логике проекта `RadioSipGateway_stage28`.

**Текущий статус: scaffold / подготовка переноса, не готовый радиошлюз.** Сейчас реализованы каркас .NET 8 Console и проверка INI-конфигурации. SIP runtime, ALSA и CM108 PTT предстоит перенести и протестировать.

- [Статус переноса](docs/PORTING_STATUS_RU.md)
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

Обычный запуск пока намеренно не выполняет SIP/радиофункции и возвращает код 3.
