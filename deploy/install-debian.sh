#!/usr/bin/env bash
set -euo pipefail

# Install a previously published build. Run from the repository root as root:
# sudo bash deploy/install-debian.sh
# This script does not configure USB/HID permissions; that must be done after CM108 identification.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH="$ROOT/artifacts/linux-x64"
if [[ ! -x "$PUBLISH/RadioSipGateway" ]]; then
  echo "Не найден $PUBLISH/RadioSipGateway. Сначала выполните dotnet publish через deploy/publish-linux-x64.sh." >&2
  exit 1
fi

if ! getent group radiosipgateway >/dev/null; then groupadd --system radiosipgateway; fi
if ! id radiosipgateway >/dev/null 2>&1; then
  useradd --system --gid radiosipgateway --home-dir /var/lib/radiosipgateway --create-home --shell /usr/sbin/nologin radiosipgateway
fi
usermod -aG audio radiosipgateway

install -d -m 0755 /opt/radiosipgateway
install -d -m 0750 -o root -g radiosipgateway /etc/radiosipgateway
install -d -m 0750 -o radiosipgateway -g radiosipgateway /var/log/radiosipgateway /run/radiosipgateway

install -m 0755 "$PUBLISH/RadioSipGateway" /opt/radiosipgateway/RadioSipGateway
find "$PUBLISH" -maxdepth 1 -type f ! -name RadioSipGateway -exec install -m 0644 {} /opt/radiosipgateway/ \;
chown -R root:root /opt/radiosipgateway
chmod 0755 /opt/radiosipgateway/RadioSipGateway

if [[ ! -e /etc/radiosipgateway/config.ini ]]; then
  install -m 0640 -o root -g radiosipgateway "$ROOT/config/radiogateway.example.ini" /etc/radiosipgateway/config.ini
  echo "Создан /etc/radiosipgateway/config.ini — обязательно отредактируйте SIP-сервер, логин и пароль."
fi

install -m 0644 "$ROOT/deploy/radiosipgateway.service" /etc/systemd/system/radiosipgateway.service
systemctl daemon-reload

echo "Установка файлов завершена. Пока что приложение поддерживает проверку конфига, но ещё не готово для работы как радиошлюз."
echo "Проверьте конфиг: sudo -u radiosipgateway /opt/radiosipgateway/RadioSipGateway --check-config --config /etc/radiosipgateway/config.ini"
echo "Не запускайте службу до завершения Linux-порта SIP/ALSA/CM108 PTT."
