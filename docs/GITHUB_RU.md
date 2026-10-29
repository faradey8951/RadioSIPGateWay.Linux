# Публикация в GitHub и получение обновлений

## Владельцу проекта: создать приватный репозиторий

1. Откройте https://github.com/new.
2. Repository name: `RadioSipGateway`.
3. Выберите `Private`.
4. Не ставьте галочки автоматического создания README, `.gitignore` и лицензии — они уже лежат в проекте.
5. Нажмите `Create repository`.

На Windows установите Git for Windows: https://git-scm.com/download/win. Распакуйте каталог `RadioSipGateway.Linux` в постоянное место. Откройте PowerShell в этом каталоге и выполните:

```powershell
git init
git add .
git status
git commit -m "Initial Linux port scaffold"
git branch -M main
git remote add origin https://github.com/OWNER/RadioSipGateway.git
git push -u origin main
```

Замените `OWNER` на свой GitHub username. Команда `git status` нужна для проверки состава коммита: убедитесь, что там нет паролей, приватных конфигов или журналов.

Затем на странице репозитория откройте `Settings` → `Collaborators` (название раздела может немного отличаться) → `Add people` и добавьте GitHub username товарища. Ему нужно принять приглашение.

## Товарищу: клонировать репозиторий

```bash
sudo apt update
sudo apt install -y git
# Если git clone запросит авторизацию, сначала настройте доступ к приватному репозиторию.
git clone https://github.com/OWNER/RadioSipGateway.git
cd RadioSipGateway
```

Для HTTPS GitHub не принимает пароль аккаунта как пароль Git. Используйте авторизацию через браузер/credential manager либо SSH-ключ, добавленный в свой GitHub-аккаунт. Не пересылайте личные токены в чате.

## Обновить исходники

```bash
cd ~/RadioSipGateway
git pull --ff-only
```

Если обновление содержит изменения кода, после завершения рабочего порта нужно будет заново выполнить публикацию, затем обновить файлы и перезапустить службу. Конфиг `/etc/radiosipgateway/config.ini` хранится отдельно и не должен перезаписываться `git pull`.

## Важно

На текущем этапе репозиторий содержит каркас и документацию, а не готовый шлюз. Не включайте службу и не подключайте проект к передатчику в ожидании рабочего PTT: сначала должны быть реализованы и проверены SIP, ALSA и CM108 HID.
