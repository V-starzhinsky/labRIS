# Лабораторная работа 4 — вариант 18 (C#, Windows Forms)

«Выделение заданного цветового диапазона в маску» («только красные пиксели»).
Асинхронная многопоточная обработка изображений: конвейер
`UI-поток → асинхронный читатель → планировщик → пул потоков → сборщик → асинхронный писатель`.

## Структура проекта (один класс — один файл)

| Файл | Назначение |
|---|---|
| `Program.cs` | точка входа приложения ([STAThread], запуск MainForm) |
| `UI/MainForm.cs` | главное окно, диалоги, async-обработчики кнопок, журнал и прогресс |
| `Core/ColorMaskProcessor.cs` | ядро конвейера: SemaphoreSlim, CountdownEvent, критическая секция, сборка полос; алгоритм варианта 18 (ProcessStrip) |
| `Core/StripPlanner.cs` | нарезка изображения на полосы с раскладкой остатка высоты |
| `Core/StripResult.cs` | неизменяемый объект «результат одной полосы» |
| `Imaging/BitmapConverter.cs` | Bitmap ↔ byte[] через LockBits/Marshal.Copy |
| `Imaging/ImageFileIO.cs` | асинхронное чтение файла и асинхронная запись PNG |
| `Threading/WorkerPool.cs` | собственный пул потоков: lock, AutoResetEvent, ManualResetEvent |

Пять механизмов синхронизации: `lock` (Monitor), `AutoResetEvent`,
`ManualResetEvent`, `SemaphoreSlim`, `CountdownEvent`.

## Сборка и запуск (Windows)

Способ 1 — JetBrains Rider: File → Open → `Variant18_ColorMask.sln`
(или просто папка проекта — Rider распознаёт SDK-проект). Убедитесь, что
установлен .NET SDK 8 (Settings → Build, Execution, Deployment → Toolset and
Build); при необходимости Rider предложит скачать SDK. Запуск — зелёная
стрелка (run-configuration создаётся автоматически). Дизайнер WinForms не
нужен: интерфейс строится кодом в `MainForm.BuildUi`. Если требуется
.NET Framework 4.7.2 — замените `TargetFramework` на `net472` и установите
.NET Framework Developer Pack; код программы менять не нужно.

Способ 2 — командная строка (.NET 8 SDK):

```
cd Variant18_ColorMask
dotnet build -c Release
dotnet run -c Release
```

Способ 3 — Visual Studio 2022: открыть sln/папку проекта и нажать F5.

## Сценарий работы программы

1. «1. Выбрать изображение...» — выбрать PNG/JPG; появится превью.
2. Выбрать целевой цвет и допуск (0 — точное совпадение, 200 — широкий диапазон).
3. «2. Обработать» — в журнале видно, как полосы изображений обрабатывают
   разные потоки пула (`PoolWorker-N: полоса #k (строки a-b) готова, i/64`).
4. «3. Отмена» — кооперативная отмена конвейера в любой момент.
5. «4. Сохранить результат...» — маска сохраняется в PNG.

Подробное описание всего процесса (проектирование → написание кода →
отладка → выполнение) — в прилагаемом документе Word.
