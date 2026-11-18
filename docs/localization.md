# Mod Hub translations

The language system works without a restart. Complete Russian, Ukrainian and English packs are built in. The list
shows only language names, without the author's name. Incomplete user packs still fall back to another translation
for missing strings.

## Making your own translation

1. Open "Settings → Interface language → Create template".
2. Save the JSON file. It already contains every current key and its source string.
3. Change `id` to your own unique identifier, `culture` to the language code, `displayName` to the language name,
   `author` to the translator's name and `version` to the translation version. Do not use the `official.` prefix.
4. Translate the values in `strings`, keeping the keys and the `{0}`, `{1}`… parameters. Do not translate the key
   names themselves. Save the file as UTF-8.
5. Use "Open translations folder" to open `Languages`, copy the file there and click "Refresh list". Choose your pack.

Translations live in the `Languages` folder next to `Mod Hub.exe`. There is no need to change the sources or install a
DLL. Several translations of one culture can coexist with different IDs and authors.

A minimal partial pack:

```json
{
  "schemaVersion": 1,
  "id": "community.uk.example",
  "culture": "uk",
  "displayName": "Українська — мій варіант",
  "author": "Автор перекладу",
  "version": "1.0",
  "strings": {
    "settings.language.title": "Мова інтерфейсу",
    "settings.language.reload": "Оновити список"
  }
}
```

Missing strings come from the official pack of the same culture, then from the Russian source catalog. A string whose
parameters do not match is skipped and the fallback is used. Damaged JSON or an incompatible schema is rejected with a
report. Built-in packs cannot be overwritten by an external ID.

## Parameters and plural forms

Parameters may be reordered, but their numbers may not be removed or added. If the source string has `{0}` and `{1}`,
the translation must keep both. `{0:N0}`, `{0:0.0}` and `{0:d MMM yyyy}` are .NET number/date formats. Literal braces
are written as `{{` and `}}`.

Instead of a string you can use an object of forms:

```json
{
  "one": "{0} файл",
  "few": "{0} файли",
  "many": "{0} файлів",
  "other": "{0} файлу"
}
```

`other` is required, and all forms must use the same set of parameters. The count is the string's first argument.
RU/UK follow their endings and the 11–14 exceptions, fractional values use `other`; English uses `one` for one.
Packs contain no expressions or program code.

## Compatibility

Pack text is shown as plain text, not as XAML/HTML. Links are not opened automatically. Service keys, paths, regular
expressions, configuration aliases, JSON schemas, extensions and file-picker patterns are not translated. In file
dialogs only the filter labels are translated; `*.zip` and other service syntax stay in code.

Mod names, authors, README files and the contents of user files are not translated. The built-in recommendations
catalog's own notes use resources; external catalogs and metadata are kept as data.

Keys look like `layer.section.meaning`: `ui.peds.loadingImages`, `core.installation.saveTable`, `cli.nothingInstall`.
The layer (`ui`, `core`, `cli`) is required: previously saved messages are matched by the `core.` and `cli.` prefixes.
When editing a text, keep the key if the meaning stays the same; a new meaning needs a new key. `catalog.*`,
`settings.language.*` and `format.*` follow the same rules. The schema version is separate from the version of a user
translation.

## For developers

- `L.T(key, args)` is Mod Hub's own text. Pass numbers and dates as typed arguments, not as pre-formatted strings.
- `L.Join`, `L.Truncate`, `L.TrimEnd` keep translation information in composite text.
- `L.Owned` is allowed only where Mod Hub's own Core/CLI messages are explicitly shown. Never use it on names,
  README files, paths or arbitrary external data.
- `L.Display` shows a cached string with an explicit token again in the current language; external text is kept.
- `L.Source` gives the Russian source text for compatible writing into the old queue without changing the chosen
  language.
- XAML: `xmlns:loc="clr-namespace:GTAModManager.App.Localization"` and `Text="{loc:Tr settings.language.title}"`.
- Programmatic WPF properties update automatically only for strings that explicitly come from `L.T`; external text
  that happens to match is not translated. For `Run` use `LiveLocalization.CreateRun`, because a text container loses
  the string's identity.
- Do not change the global CurrentCulture: reading old configurations depends on it. Display is formatted with the
  chosen pack's own culture.

Checks:

```powershell
dotnet test tests/GTAModManager.Core.Tests/GTAModManager.Core.Tests.csproj -m:1 -nr:false -p:NuGetAudit=false
dotnet run --project tests/GTAModManager.Localization.UiHarness/GTAModManager.Localization.UiHarness.csproj -p:NuGetAudit=false
```

In a sandbox the test process may need permission for a local IPC connection. The existing CA1416 warnings in Core
concern Windows APIs and are not caused by localization.

Live switching also walks the logical and visual tree of every open window, including hidden pages and context menus.
A single class handler for Loaded is not enough: WPF does not send that event to every programmatic child element. The
UI harness shows a real window and checks repeated RU/EN/UK switches, not only detached controls.
