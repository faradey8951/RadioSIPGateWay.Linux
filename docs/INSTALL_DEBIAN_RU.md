# Подготовка Debian x86-64

> Это инструкция для подготовки среды и проверки первого этапа. Текущий runtime выполняет только SIP-регистрацию; звонки, аудио и PTT ещё не готовы. Не подключайте реальный передатчик к работающей службе на этой стадии.

## 1. Проверить систему

```bash
uname -m
cat /etc/debian_version
```

Для целевой сборки ожидается `x86_64`.

## 2. Установить базовые инструменты

```bash
sudo apt update
sudo apt install -y git curl ca-certificates build-essential pkg-config alsa-utils libasound2-dev
```

.NET SDK 8 нужно установить из официального репозитория Microsoft для своей версии Debian. После установки проверить:

```bash
dotnet --info
```

## 3. Получить исходники

После публикации репозитория владельцем:

```bash
git clone https://github.com/faradey8951/RadioSIPGateWay.Linux.git
cd RadioSIPGateWay.Linux
```

Репозиторий сейчас публичный, поэтому клонирование не требует добавления в Collaborators. Если позже сделать его приватным, аккаунт товарища нужно добавить в Collaborators.

## 4. Собрать linux-x64

```bash
bash deploy/publish-linux-x64.sh
```

Результат будет в `artifacts/linux-x64/`.

## 5. Проверить конфигурацию и SIP-регистрацию (первый этап)

```bash
cp config/radiogateway.example.ini /tmp/radiogateway.ini
nano /tmp/radiogateway.ini
./artifacts/linux-x64/RadioSipGateway --check-config --config /tmp/radiogateway.ini
```

После указания реальных SIP-реквизитов можно проверить только регистрацию (пока без звонков и аудио):

```bash
./artifacts/linux-x64/RadioSipGateway --config /tmp/radiogateway.ini
```
Остановка — `Ctrl+C`. В этом этапе не подключайте радиопередатчик: PTT ещё не реализован.

Не записывайте настоящий пароль в публичный репозиторий. Для реального развёртывания файл конфигурации должен быть доступен только root и сервисной группе.

## 6. Определить аудио и CM108 (сбор диагностических данных)

```bash
arecord -L
aplay -L
lsusb
ls -l /dev/hidraw* 2>/dev/null || true
```

Для конкретной CM108 дополнительно собрать:

```bash
lsusb -nn
for d in /dev/hidraw*; do
  [ -e "$d" ] || continue
  echo "=== $d ==="
  udevadm info --query=all --name="$d" | grep -E 'ID_VENDOR_ID|ID_MODEL_ID|ID_VENDOR=|ID_MODEL=|ID_USB_INTERFACE_NUM' || true
done
```

Передайте результаты разработчику без персональных данных и без паролей. Эти сведения нужны для выбора точного HID-интерфейса; номер `hidraw` может меняться после перезагрузки, поэтому финальная настройка должна использовать устойчивое правило udev или поиск по USB-идентификаторам.

## Проверка DTMF-декодера (без радио)

После сборки можно выполнить синтетический тест без подключённой звуковой карты:

```bash
./RadioSipGateway --self-test-dtmf
```

Ожидаемый результат начинается с `DTMF self-test OK`. Эта проверка подтверждает только DSP-декодер, не работу живого ALSA-аудиотракта.
