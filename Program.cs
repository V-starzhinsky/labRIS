// ============================================================================
// Лабораторная работа 4. Вариант 18 — «Выделение заданного цветового
// диапазона в маску». Файл: Program.cs (ТОЧКА ВХОДА ПРИЛОЖЕНИЯ).
//
// Проект разбит на классы по папкам:
//   Core/      — ядро обработки: конвейер, нарезка на полосы, модель результата;
//   Imaging/   — преобразования Bitmap <-> byte[] и асинхронный ввод-вывод;
//   Threading/ — собственный пул потоков;
//   UI/        — главное окно (Windows Forms).
//
// Один и тот же код собирается на .NET 8 (net8.0-windows, по умолчанию в
// csproj) и на .NET Framework 4.7.2 (замените TargetFramework в csproj).
// ============================================================================
using System;
using System.Windows.Forms;
using Lab4Variant18.UI;

namespace Lab4Variant18
{
    // Стандартная точка входа Windows Forms. Атрибут [STAThread] ОБЯЗАТЕЛЕН:
    // WinForms и GDI+ (диалоги, буфер обмена, ряд операций Bitmap) рассчитаны
    // на однопоточное апартаментное состояние (STA) — без него диалоги файлов
    // и буфер обмена работают некорректно.
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // Включаем визуальные стили Windows (кнопки и поля в стиле темы ОС).
            Application.EnableVisualStyles();
            // Фиксируем единый рендеринг текста во всех контролах
            // (UseCompatibleTextRendering = false — современный режим GDI).
            Application.SetCompatibleTextRenderingDefault(false);
            // Создаём главное окно и запускаем цикл сообщений UI-потока:
            // отсюда и до закрытия формы работает «поток интерфейса».
            Application.Run(new MainForm());
        }
    }
}
