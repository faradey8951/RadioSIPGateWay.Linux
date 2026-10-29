# Подготовка Debian x86-64

> Это инструкция для подготовки среды и проверки конфигурации. Сам Linux-радиошлюз ещё не завершён; не запускайте передачу в эфир на этой стадии.

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

После публикации приватного репозитория владельцем:

```bash
git clone https://github.com/OWNER/RadioSipGateway.git
cd RadioSipGateway
```

Замените `OWNER` на имя владельца репозитория. Для приватного репозитория аккаунт товарища должен быть добавлен в Collaborators.

## 4. Собрать linux-x64

```bash
bash deploy/publish-linux-x64.sh
```

Результат будет в `artifacts/linux-x64/`.

## 5. Проверить конфигурацию (на текущем этапе)

```bash
cp config/radiogateway.example.ini /tmp/radiogateway.ini
nano /tmp/radiogateway.ini
./artifacts/linux-x64/RadioSipGateway --check-config --config /tmp/radiogateway.ini
```

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
