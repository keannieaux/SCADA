# Настройка Visual Studio Code для разработки на C# (проект SCADA)

Это руководство описывает минимальную настройку VS Code для комфортной разработки на C# и Avalonia. Оно актуально, если вы привыкли к Rider или Visual Studio, где многие вещи делаются автоматически.

## Требования

Установите [.NET SDK](https://dotnet.microsoft.com/download) версии, соответствующей `TargetFramework` в `SCADA.csproj` (сейчас `net10.0`).

Проверить установку:

```bash
dotnet --version
dotnet --list-sdks
```

## Расширения VS Code

Установите следующие расширения:

| Расширение | Назначение |
|---|---|
| **C# Dev Kit** (Microsoft) | Главное расширение для C#: IntelliSense, отладка, Solution Explorer, тесты. |
| **C#** (Microsoft) | Языковой сервис, обычно ставится вместе с C# Dev Kit. |
| **Avalonia for VSCode** | Поддержка `.axaml`: IntelliSense и превью. |
| **NuGet Gallery** | Установка NuGet-пакетов через интерфейс. |
| **Error Lens** | Показ ошибок и предупреждений прямо на строке кода. |
| **GitLens** | Удобная работа с git. |

Опционально:

- **Roslynator** — дополнительные рефакторинги C#.
- **XML Tools** — форматирование XML/AXAML.
- **Todo Tree** — поиск `TODO`/`FIXME` по проекту.

## Файлы конфигурации `.vscode`

В корне проекта находится папка `.vscode` с общими настройками. Она закоммичена в git, поэтому настройки будут одинаковыми у всех, кто откроет проект.

### `settings.json`

Отвечает за внешний вид и базовое поведение редактора.

Важная часть — группировка файлов `.axaml` и `.axaml.cs` в Explorer (аналог dependent files в Rider/VS):

```json
{
    "explorer.fileNesting.enabled": true,
    "explorer.fileNesting.expand": false,
    "explorer.fileNesting.patterns": {
        "*.axaml": "${capture}.axaml.cs"
    }
}
```

### `tasks.json`

Задачи для сборки и очистки проекта. Основная задача `build` запускается по `Ctrl+Shift+B`.

Доступные задачи:

- `build` — сборка solution в конфигурации Debug.
- `build-release` — сборка в Release.
- `clean` — очистка артефактов сборки.

### `launch.json`

Конфигурация запуска. Нажатие `F5` сначала выполняет задачу `build`, затем запускает приложение под отладчиком.

## `.editorconfig`

Файл в корне проекта задаёт единый стиль оформления кода:

- кодировка `utf-8`, окончания строк `crlf`;
- 4 пробела отступа для `.cs` и `.axaml`;
- фигурные скобки на новой строке;
- `var` — когда тип очевиден;
- приватные поля — `camelCase` с префиксом `_`.

Чтобы правила применялись при сохранении, включите в VS Code:

```json
"editor.formatOnSave": true
```

## Работа с solution

В отличие от Rider/VS, в VS Code проекты в solution управляются через командную строку.

### Создать новый проект и добавить в solution

```bash
dotnet new classlib -n SCADA.Core -o SCADA.Core
dotnet sln add SCADA.Core/SCADA.Core.csproj
```

### Добавить ссылку между проектами

```bash
dotnet add SCADA/SCADA.csproj reference SCADA.Core/SCADA.Core.csproj
```

### Посмотреть состав solution

```bash
dotnet sln list
```

## Запуск и отладка

| Действие | Способ |
|---|---|
| Собрать проект | `Ctrl+Shift+B` |
| Запустить с отладкой | `F5` |
| Запустить без отладки | `Ctrl+F5` |
| Запустить из терминала | `dotnet run --project SCADA` |

## Полезные команды

```bash
# Сборка
dotnet build

# Сборка в Release
dotnet build -c Release

# Очистка
dotnet clean

# Восстановление NuGet-пакетов
dotnet restore

# Запуск
dotnet run --project SCADA

# Слежение за изменениями и автоперезапуск (hot reload)
dotnet watch --project SCADA
```

## Если что-то не работает

### IntelliSense не появился

1. Проверьте, что открыта именно папка с `SCADA.sln`, а не подпапка.
2. Дождитесь окончания загрузки C# Dev Kit (в строке состояния есть индикатор).
3. Выполните `dotnet restore`.

### `F5` не запускает приложение

1. Убедитесь, что `launch.json` существует и в нём правильный `projectPath`.
2. Проверьте, что проект собирается без ошибок: `Ctrl+Shift+B`.
3. Перезагрузите окно VS Code: `Ctrl+Shift+P` → `Developer: Reload Window`.

### Файлы `.axaml` и `.axaml.cs` не группируются

1. Проверьте, что в `settings.json` нет опечаток.
2. Перезагрузите окно VS Code.
3. Убедитесь, что файлы имеют одинаковое базовое имя, например `MainWindow.axaml` и `MainWindow.axaml.cs`.

## Ссылки

- [C# Dev Kit в VS Code](https://code.visualstudio.com/docs/csharp/get-started)
- [Avalonia UI](https://docs.avaloniaui.net/)
- [EditorConfig](https://editorconfig.org/)
