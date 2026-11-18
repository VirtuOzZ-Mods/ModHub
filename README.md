# Mod Hub by VirtuOzZ

Менеджер модов для GTA V **Legacy** и **Enhanced** под Windows. Его цель — сделать моддинг GTA проще для
новичков: то, что опытные игроки делают вручную по инструкциям авторов модов, Mod Hub старается делать сам.
A mod manager for GTA V **Legacy** and **Enhanced** on Windows. [English below](#english).

> **Версия 0.4 beta.** Это бета: возможны ошибки, сообщайте о них в [issues](../../issues). Проект развивает один человек — VirtuOzZ, но он открыт: исправления, новые функции и
> переводы присылайте pull request'ами (см. [«Участие в разработке»](#участие-в-разработке)).
> **Важно:** Mod Hub проверялся только на чистой игре. Как он поведёт себя, если в GTA V или GTA V Enhanced
> уже установлены моды (вручную, через OpenIV и т. п.), ещё не тестировалось. Перед использованием сделайте
> резервную копию игры.

## Возможности

- **Установка модов** из OIV-пакетов и обычных архивов (ZIP, RAR, 7Z): add-on DLC, ASI-плагины, скрипты.
- **Папка `mods\`**: оригинальные файлы игры не изменяются, моды подключаются через загрузчик.
- **База для моддинга одной кнопкой**: Script Hook V, загрузчик папки `mods` (RageOpenV), ScriptHookVDotNet,
  расширители лимитов (HeapAdjuster, Packfile/Modkit/StaticBounds Limit Adjuster); распознаёт Resource Extensions.
- **История и отмена**: каждая установка выполняется как транзакция с резервной копией, её можно отменить.
- **Возврат к чистой игре**: удаляет все моды и их следы, оригиналы Rockstar не трогает.
- **Каталог модов** с проверкой совместимости с Legacy и Enhanced.
- **Add-On PED** для Legacy со своим селектором персонажей.
- **NaturalVision Evolved / Enhanced и LCPP**: установка выбранных компонентов из архива, который вы скачали
  сами. На Enhanced Mod Hub ставит загрузчик NVE и переносит моды из `mods\` в папку onigiri, чтобы NVE и
  остальные моды работали вместе.
- **Горячие клавиши модов**: клавиатура и геймпад (подписи Xbox или PlayStation), поиск конфликтов,
  переназначение и отключение любой клавиши.
- **Звуки двигателя** для add-on машин: таблица машин, один звук сразу нескольким.
- **Включение и отключение модов** без удаления.
- **Интерфейс** на русском, английском и украинском.
- **Без установки**: программа и все её данные лежат в одной папке, в AppData ничего не пишется.

Игра определяется автоматически (Rockstar Games Launcher, Steam, Epic Games).

## Скачать и запустить

1. Скачайте `ModHub-portable.zip` со страницы [Releases](../../releases).
2. Распакуйте архив в корень системного диска — получится `C:\Mod Hub by VirtuOzZ`. Это и есть
   установленная программа. Не распаковывайте в папку игры, в Program Files (туда нельзя писать без
   прав администратора) и в папки, которые синхронизирует OneDrive.
3. Запустите `C:\Mod Hub by VirtuOzZ\Mod Hub.exe`. Устанавливать .NET не нужно, всё уже внутри.
   Сборка не подписана: если Windows SmartScreen предупредит, нажмите «Подробнее» → «Выполнить в любом случае».

Требования: Windows 10/11 x64, GTA V Legacy или Enhanced. Для встроенного браузера каталога
нужен Microsoft Edge WebView2 Runtime (в Windows 11 он уже есть).

## Где Mod Hub хранит данные

Mod Hub ничего не устанавливает в систему. Всё, что он пишет для себя, лежит в его папке:

```
Mod Hub by VirtuOzZ\
  Mod Hub.exe      запуск
  README RU.txt    краткая инструкция (README EN.txt — по-английски)
  Settings.json    настройки (создаётся при первом запуске)
  Languages\       свои переводы интерфейса
  Program\         файлы программы и .NET
  Data\            ключи игры, кэш, встроенный браузер, загрузки, временные файлы
```

Кроме папки программы Mod Hub пишет только:

- **в папку игры** — сами моды, загрузчики и ASI-плагины, которые вы устанавливаете;
- **в папку `ModHub` в корне диска игры** (`X:\ModHub\Legacy`, `X:\ModHub\Enhanced`) — журнал установок,
  отключённые моды и копии для отмены. Она должна быть на том же диске, что и игра: так установка и
  отмена выполняются мгновенным переименованием, без копирования гигабайтов. Там же по умолчанию
  лежат резервные копии (`X:\ModHub\Backups`), папку можно сменить;
- **в реестр** (`HKCU\Software\Classes`) — только если вы сами включите открытие `.oiv` двойным щелчком.

Папка «Документы\Rockstar Games» только читается. Если папка программы недоступна для записи
(например, распакована в Program Files), данные идут в «Документы\ModHub».

## Как удалить

1. Если хотите вернуть игру к исходному состоянию — в Mod Hub нажмите «Вернуть ванильную игру»
   (или отмените нужные установки в «История и отмена»).
2. Если включали открытие `.oiv` двойным щелчком — выключите его в настройках.
3. Удалите папку `Mod Hub by VirtuOzZ`.
4. Удалите папку `ModHub` в корне диска игры. Только после шага 1: в ней лежат копии для отмены
   и отключённые моды.

## Как это работает с ключами игры

Архивы RPF зашифрованы. Mod Hub **не содержит** ключей шифрования и файлов игры: ключи
ищутся в исполняемом файле вашей копии игры (как это делает CodeWalker) и хранятся только у вас
на компьютере. При первой установке скачиваются RageOpenV (официальный релиз авторов на GitHub,
проверяется по SHA-256), Script Hook V (dev-c.com), ScriptHookVDotNet (GitHub) и `magic.dat` из
проекта CodeWalker (GitHub). Сторонние программы Mod Hub не распространяет: их всегда скачивают
с официальных страниц авторов.

## Сборка из исходников

Нужно:
- Windows 10/11 x64;
- [.NET SDK 10](https://dotnet.microsoft.com/download) (версия задана в `global.json`);
- для нативных компонентов (`native/`) — Visual Studio 2022 с C++ и CMake. Для сборки самого
  приложения они не нужны: готовые `.asi` и запускатор уже лежат в репозитории.

```powershell
dotnet build GTAModManager.slnx -c Release
dotnet test GTAModManager.slnx
```

Сборка для пользователей (тесты → публикация → архив `build\ModHub-portable.zip`):

```powershell
.\scripts\publish-portable.ps1
```

## Структура

| Папка | Что внутри |
|---|---|
| `src/GTAModManager.App` | Приложение WPF |
| `src/GTAModManager.Core` | Логика: RPF7, OIV, транзакции, бэкапы, каталог, локализация |
| `src/GTAModManager.Cli` | Консольная утилита `gmm` |
| `native/ModHubLauncher` | Запускатор `Mod Hub.exe`, который открывает `Program\ModHub.exe` |
| `native/GMMPedSelector` | ASI-плагин выбора Add-On PED |
| `tests` | Тесты |
| `docs` | [Документация для разработчиков](docs/developer/developer-guide.ru.md) ([English](docs/developer/developer-guide.en.md)), описание системы переводов, инструкция для пользователей |

## Участие в разработке

Mod Hub — открытый проект. Его ведёт VirtuOzZ, но помогать может любой:

- нашли ошибку — откройте [issue](../../issues): что делали, что ожидали, что получилось, версия игры
  (Legacy или Enhanced) и номер сборки из настроек Mod Hub;
- хотите исправить или добавить — присылайте pull request. Перед этим загляните в
  [документацию для разработчиков](docs/developer/developer-guide.ru.md) и в [CONTRIBUTING.md](CONTRIBUTING.md);
- переводы интерфейса — см. [docs/localization.md](docs/localization.md).

## Лицензия

Код Mod Hub — [MIT](LICENSE). Сторонние компоненты (шрифт Inter и другие) и программы, которые
Mod Hub скачивает при установке (RageOpenV — GPL-3.0, Script Hook V, ScriptHookVDotNet), сохраняют
свои лицензии — см. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Проект не связан с Rockstar Games и Take-Two Interactive. Не используйте моды в GTA Online.

---

## English

Mod Hub is a Windows mod manager for GTA V Legacy and Enhanced. Its goal is to make GTA modding easier for
newcomers: what veterans do by hand from each mod's instructions, Mod Hub tries to do for you.

**Version 0.4 beta.** This is a beta: bugs are possible, please report them in [issues](../../issues). Mod Hub is developed by one person, VirtuOzZ, and it is open: fixes, new features and
translations are welcome as pull requests (see [Contributing](#contributing)). **Important:** Mod Hub has only
been tested on clean games. Using it on a GTA V or GTA V Enhanced that already has mods installed (by hand,
with OpenIV, etc.) has not been tested yet — back up your game before using it.

**Features:** installs OIV packages and regular archives (ZIP/RAR/7Z) through the `mods\` overlay
without touching original game files; one-click modding essentials (Script Hook V, RageOpenV,
ScriptHookVDotNet, limit adjusters; Resource Extensions is recognised); transactional installs with
backups and undo; restore to a clean game; a curated mod catalog with Legacy/Enhanced compatibility;
Add-On PEDs for Legacy; NaturalVision Evolved / Enhanced and LCPP installers (from archives you
downloaded yourself) — on Enhanced Mod Hub installs NVE's ASI loader and mirrors `mods\` into the onigiri
folder so NVE and other mods work together; hotkeys for keyboard and gamepad (Xbox or PlayStation labels)
with conflict search, remapping and switching keys off; engine sounds for add-on cars; mod toggle;
Russian, English and Ukrainian UI; no installation.

**Download and run:** get `ModHub-portable.zip` from [Releases](../../releases), unpack it to the root of the system drive
(`C:\Mod Hub by VirtuOzZ`; not into the game folder, Program Files or a OneDrive-synced folder) and run `Mod Hub by VirtuOzZ\Mod Hub.exe`. .NET is bundled. Windows 10/11 x64.

**Where it writes:** everything ModHub keeps for itself stays in its own folder (`Settings.json`, `Languages\`,
`Program\`, `Data\`). Besides that it writes only the mods you install into the game folder, a service
folder `X:\ModHub\<Edition>` on the game's drive (install journal, disabled mods, undo copies, default
backups; it must share the game's drive so installs and undo are instant renames) and, only if you turn it on,
the `.oiv` file association in `HKCU\Software\Classes`.

**Uninstall:** restore a clean game in ModHub if you want to, turn off the `.oiv` association if you enabled it,
then delete the `Mod Hub by VirtuOzZ` folder and the `ModHub` folder on the game's drive.

**Game keys:** no encryption keys or game files are shipped. Keys are read from your own copy of
the game and stay on your computer.

**Third-party programs are not shipped either:** RageOpenV (the authors' official GitHub release, checked by
SHA-256), Script Hook V (dev-c.com) and ScriptHookVDotNet (GitHub) are downloaded from their authors' pages
when you install them.

**Build:** Windows x64, .NET SDK 10 (see `global.json`).

```powershell
dotnet build GTAModManager.slnx -c Release
dotnet test GTAModManager.slnx
```

## Contributing

Mod Hub is an open project led by VirtuOzZ, and anyone can help: report bugs in [issues](../../issues)
(what you did, what you expected, what happened, Legacy or Enhanced, and the build number from Mod Hub's
settings), send pull requests with fixes or features (read the
[developer guide](docs/developer/developer-guide.en.md) and [CONTRIBUTING.md](CONTRIBUTING.md) first), or
add a translation ([docs/localization.md](docs/localization.md)).

**License:** Mod Hub code is [MIT](LICENSE). Third-party components keep their own licenses — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Not affiliated with Rockstar Games or
Take-Two Interactive. Do not use mods in GTA Online.
