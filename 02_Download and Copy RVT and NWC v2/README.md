# 02. Download and Copy RVT and NWC (v2)

Внутреннее имя пайплайна: **DownloadAndCopyRVTNWC**.

Ночной/регламентный пайплайн обмена моделями: забирает модели с RevitServer,
конвертирует их в NWC, раскладывает по файловому серверу компании и файловому
серверу заказчика, а также забирает встречные модели смежников.

Актуальная версия: **2.3 (13.08.2026)** — исправлены критичные проблемы
первой версии, добавлено подключение VPN, переработан редактор настроек,
исправлена кодировка вывода утилит, см. [CHANGELOG](CHANGELOG.md).

Шаг 1 (скачивание с RevitServer) прогнан вживую на пилотном проекте:
12 из 13 моделей скачаны, одна крупная упёрлась в таймаут 1 час. Ещё не
прогонялись: шаг 2 (конвертация в NWC, Navisworks) и фактическое
подключение VPN.

Предыдущая версия (1.3 от 12.07.2025) — то, что сейчас работает в регламенте;
она заморожена как архив в папке
[01_Download and Copy RVT and NWC v1](../01_Download%20and%20Copy%20RVT%20and%20NWC%20v1/).

## Состав

| Файл | Роль |
|------|------|
| `00_Start_RunScript.ps1` | Оркестратор. Читает `config.json`, готовит папки и лог, последовательно запускает выбранные шаги |
| `01_Step1_DownloadFromRevitServer.ps1` | Выгрузка моделей с RevitServer через `RevitServerTool.exe createLocalRVT` |
| `02_Step2_ConvertToNWC.ps1` | Конвертация RVT → NWC через `FiletoolsTaskRunner.exe` |
| `03_Step3_UploadToPartnersNWC.ps1` | Копирование NWC на файловый сервер заказчика |
| `04_Step4_UploadToPartnersRVT.ps1` | Копирование RVT на файловый сервер заказчика |
| `05_Step5_UploadToFileServerNWC.ps1` | Копирование NWC на внутренний файловый сервер |
| `06_Step6_SyncFromPartnersNWC.ps1` | Забор NWC смежников с сервера заказчика на внутренний сервер |
| `07_Step7_SyncFromPartnersRVT.ps1` | Забор RVT смежников с сервера заказчика на внутренний сервер |
| `log.ps1` | Функции логирования `Write-Log`, `Write-LogTop`, `Write-LogDivider`, счётчик ошибок |
| `vpn.ps1` | Подключение и отключение VPN перед работой пайплайна (SoftEther или встроенный VPN Windows). Начиная с version 2.1 |
| `common.ps1` | Общие функции: `Invoke-ExternalProcess` (безопасный запуск утилит), `Get-CleanPatternList` (нормализация фильтров). Начиная с version 2 |
| `config.json` | Все настройки пайплайна (боевой; в поставку не идёт) |
| `config.template.json` | Обезличенный шаблон настроек — идёт в `dist/` как `config.json` |
| `Settings.html` | Редактор `config.json`: схема маршрута с включением шагов, проверка настроек, теговые фильтры. Работает офлайн, внешних запросов нет |
| `docs/Инструкция.md` | Инструкция пользователя (исходник, картинки в `docs/images/`). PDF собирается в `dist/` скриптом `build.ps1` |

## Поток данных

```
RevitServer ──[Step1]──> LocalRVTFolder ──[Step2]──> LocalNWCFolder
                              │                            │
                              │                            ├──[Step3]──> PartnersNWCFolder
                              │                            └──[Step5]──> FileServerNWCFolder
                              └──[Step4]──> PartnersRevitFolder

PartnersNWCFolder   ──[Step6]──> FileServerNWCFolder
PartnersRevitFolder ──[Step7]──> FileServerRevitFolder
```

## Запуск

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File "00_Start_RunScript.ps1"
```

Штатно запускается Планировщиком заданий Windows на рабочей станции BIM-отдела.
Логи пишутся в подпапку `Logs` рядом со скриптом. Код возврата: `0` — ошибок нет,
`1` — в логе есть записи уровня ERROR (в version 1 код возврата не выставлялся).

Набор выполняемых шагов задаётся ключом `SelectedScripts` в `config.json` —
шаги можно включать и выключать без правки кода.

## Ключи config.json

| Ключ | Смысл |
|------|-------|
| `RevitServerName` | IP или имя RevitServer |
| `RevitModelsPath[]` | Пути моделей на RevitServer (относительно корня хранилища) |
| `LocalRVTFolder`, `LocalNWCFolder` | Локальные временные папки; при пустом значении создаются `RVT`/`NWC` рядом со скриптом |
| `RevitServerToolPath`, `NavisworksToolPath` | Пути к CLI-утилитам Autodesk |
| `FileServerRevitFolder`, `FileServerNWCFolder` | Папки внутреннего файлового сервера |
| `PartnersRevitFolder`, `PartnersNWCFolder` | Папки файлового сервера заказчика |
| `SuffixRVTPartners`, `SuffixNWCPartners` | Суффикс, добавляемый к имени файла при выгрузке |
| `ModelsRVTFromPartners[]`, `ModelsNWCFromPartners[]` | Подстроки имён моделей, которые забираем у смежников |
| `StopUploadModelsToPartners[]` | Подстроки имён, которые **не** выгружаем заказчику |
| `SelectedScripts[]` | Какие шаги выполнять и в каком порядке |
| `Vpn` | Блок настроек VPN, см. ниже |

## VPN

RevitServer и файловые серверы заказчика доступны только через VPN. Начиная
с version 2.1 пайплайн умеет поднимать соединение сам — это нужно для запуска
из Планировщика, где подключить VPN вручную некому.

```json
"Vpn": {
  "Enabled": false,
  "Type": "SoftEther",
  "VpnCmdPath": "C:\\Program Files\\SoftEther VPN Client\\vpncmd.exe",
  "AccountName": "Проект-VPN",
  "RasEntryName": "",
  "PasswordEnvVar": "",
  "ConnectTimeoutSeconds": 90,
  "TestHost": "PARTNER-NAS",
  "TestPort": 445,
  "DisconnectAfterRun": true
}
```

| Ключ | Смысл |
|------|-------|
| `Enabled` | Поднимать ли VPN. `false` — пайплайн работает как раньше |
| `Type` | `SoftEther` (через `vpncmd.exe`) или `Rasdial` (встроенный VPN Windows) |
| `VpnCmdPath` | Путь к `vpncmd.exe`, только для SoftEther |
| `AccountName` | Имя подключения в клиенте SoftEther (см. список ниже) |
| `RasEntryName` | Имя подключения Windows, только для `Rasdial` |
| `PasswordEnvVar` | Имя переменной окружения с паролем управления клиентом. Обычно пусто. **Сам пароль в config.json не хранится** |
| `ConnectTimeoutSeconds` | Сколько ждать готовности канала |
| `TestHost`, `TestPort` | Узел, доступностью которого подтверждается подключение. При `TestPort: 0` — проверка ping |
| `DisconnectAfterRun` | Разрывать ли соединение после работы |

Логика: соединение, поднятое **вручную до** запуска пайплайна, по завершении
не разрывается. Если VPN не поднялся — шаги не выполняются, пайплайн возвращает
код 1 (иначе прогон отработал бы вхолостую).

Посмотреть имена подключений SoftEther:

```bash
"C:\Program Files\SoftEther VPN Client\vpncmd.exe" /client localhost /cmd AccountList
```

### Запуск из Планировщика заданий

- Служба **SoftEther VPN Client (`SEVPNCLIENT`)** должна быть запущена и в режиме
  автозапуска — пайплайн не стартует её сам, а пишет об этом ошибку в лог.
  Настройки подключений хранятся в самой службе, поэтому работают и без входа
  пользователя в систему.
- В задаче выбрать «Выполнять вне зависимости от регистрации пользователя».
- Задача считается неуспешной по коду возврата `1` — на него можно повесить
  повторный запуск или уведомление.
- Учётной записи задачи нужен доступ к сетевым папкам из `config.json`.

## Внешние зависимости

- Autodesk Revit 2021 → `RevitServerTool.exe`
- Autodesk Navisworks Manage 2021 → `FiletoolsTaskRunner.exe`
- Доступ по SMB к `\\FILESRV\...` и `\\PARTNER-NAS\...`
- Доступ к RevitServer `RVTSRV`

## Сборка поставки для контрагентов

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1"
```

Скрипт `build.ps1` собирает папку `dist/` для передачи наружу:

- удаляет комментарии из `.ps1` и `Settings.html`, добавляет лицензионный заголовок;
- вместо боевого `config.json` кладёт **обезличенный шаблон** `src/config.template.json`
  (пустые адреса серверов, пути и фильтры; стандартные пути к утилитам и набор
  шагов сохранены как значения по умолчанию);
- пересобирает `Инструкция.pdf` из `docs/Инструкция.md`.

Требуется Microsoft Edge или Google Chrome (для PDF). Исходники в `src/` не меняются —
правим их, а `dist/` только пересобираем. Боевой `config.json` с реальными адресами
остаётся в `src/` и в поставку **не попадает**.

> Если в `config.json` добавили новый ключ, добавьте его и в `config.template.json` —
> при сборке `build.ps1` сверяет ключи и предупредит, если шаблон отстал.

## Документы

- Инструкция пользователя: [Markdown](docs/Инструкция.md) (PDF собирается в `dist/` через `build.ps1`)
- [Что изменилось](CHANGELOG.md)
