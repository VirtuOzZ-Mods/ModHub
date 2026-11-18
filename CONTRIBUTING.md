# Contributing to Mod Hub

Mod Hub is developed and maintained by **VirtuOzZ**. It is an open project: bug reports, fixes, new
features and translations from anyone are welcome. VirtuOzZ reviews and merges pull requests and keeps
the overall direction of the project.

[Русская версия ниже](#участие-в-разработке-mod-hub).

## Reporting a bug

Open an [issue](../../issues) with:

- what you did, what you expected and what happened;
- GTA V Legacy or Enhanced, and the game version;
- the Mod Hub build number (Settings, the line under the title);
- the crash report from `Mod Hub by VirtuOzZ\Data\Logs` if Mod Hub showed an unexpected error.

## Sending a pull request

1. Fork the repository and create a branch from `main`.
2. Read the [developer guide](docs/developer/developer-guide.en.md): where Mod Hub may write, how game
   changes go through transactions (`gmm.exe`), and how the interface is localized.
3. Keep the rules of the project:
   - every change to the game is an undoable transaction; Mod Hub never edits original Rockstar files;
   - Mod Hub writes for itself only through `AppPaths` and `GameInstall.WorkDir`;
   - every user-facing string goes through `L.T(...)` with entries in `ru.json`, `en.json` and `uk.json`;
   - code comments in English; format with `dotnet csharpier format .`;
   - add or update tests in `tests/GTAModManager.Core.Tests`.
4. Run `dotnet test GTAModManager.slnx` — all tests must pass.
5. Open the pull request with a short description of what changed and why.

Third-party mods and tools are never added to the repository: Mod Hub downloads them from their authors'
pages. Please respect their licences.

---

## Участие в разработке Mod Hub

Mod Hub разрабатывает и поддерживает **VirtuOzZ**. Проект открытый: сообщения об ошибках, исправления,
новые функции и переводы может присылать любой. VirtuOzZ проверяет и принимает pull request'ы и
определяет направление развития проекта.

**Ошибка** — откройте [issue](../../issues): что делали, что ожидали, что получилось; Legacy или Enhanced и
версия игры; номер сборки Mod Hub (Настройки, строка под заголовком); отчёт из
`Mod Hub by VirtuOzZ\Data\Logs`, если было сообщение о непредвиденной ошибке.

**Pull request** — сделайте форк и ветку от `main`, прочитайте
[документацию для разработчиков](docs/developer/developer-guide.ru.md) и соблюдайте правила проекта:
любое изменение игры — отменяемая транзакция, оригинальные файлы Rockstar не меняются; Mod Hub пишет
для себя только через `AppPaths` и `GameInstall.WorkDir`; все тексты интерфейса — через `L.T(...)` с
переводами в `ru.json`, `en.json` и `uk.json`; комментарии в коде на английском; форматирование
`dotnet csharpier format .`; тесты в `tests/GTAModManager.Core.Tests`. Перед отправкой
`dotnet test GTAModManager.slnx` должен проходить полностью.

Сторонние моды и программы в репозиторий не добавляются: Mod Hub скачивает их со страниц авторов.
