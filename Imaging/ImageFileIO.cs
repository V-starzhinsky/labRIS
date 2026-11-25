// ============================================================================
// Лабораторная работа 4. Вариант 18. Файл: Imaging/ImageFileIO.cs
//
// АСИНХРОННЫЙ ВВОД-ВЫВОД ИЗОБРАЖЕНИЙ: чтение файла с диска и запись
// результата в PNG. Все операции построены на async/await и используют
// асинхронный режим ОС (overlapped I/O), поэтому ни UI-поток, ни потоки
// пула не занимаются на время дисковых операций.
// ============================================================================
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lab4Variant18.Imaging
{
    // Статический сервис ввода-вывода: методов всего два — «прочитать»
    // и «сохранить». Оба возвращают Task, т.е. подписаны под await.
    internal static class ImageFileIO
    {
        // ------------------------------------------------------------------
        // АСИНХРОННОЕ ЧТЕНИЕ ФАЙЛА -> готовый Bitmap.
        // FileStream создаётся с useAsync:true — это асинхронный режим
        // ввода-вывода ОС (overlapped I/O): на время дисковой операции не
        // занимается ни UI-поток, ни поток пула. Декодирование изображения
        // тоже выполняется в фоне (Task.Run).
        // ------------------------------------------------------------------
        public static async Task<Bitmap> LoadImageAsync(string path,
            CancellationToken token)
        {
            byte[] bytes;
            using (FileStream fs = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                // Читаем файл целиком в память по частям: ReadAsync может
                // вернуть меньше байт, чем запрошено, поэтому цикл до конца.
                bytes = new byte[fs.Length];
                int pos = 0;
                while (pos < bytes.Length)
                {
                    int n = await fs.ReadAsync(bytes, pos, bytes.Length - pos, token);
                    if (n == 0) break; // EOF — больше данных нет
                    pos += n;
                }
            }

            // Декодируем картинку вне UI-потока: new Bitmap(stream) для
            // больших JPEG/PNG занимает заметное время, а UI обязан
            // оставаться отзывчивым на весь период обработки.
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); // вежливая отмена до работы
                MemoryStream ms = new MemoryStream(bytes); // НЕ закрываем:
                return new Bitmap(ms);                     // Bitmap требует
            }, token);                                     // живой поток данных
        }

        // ------------------------------------------------------------------
        // АСИНХРОННАЯ ЗАПИСЬ РЕЗУЛЬТАТА: сначала кодируем PNG в память
        // (фоновый поток — Task.Run), затем пишем файл через WriteAsync.
        // Двухшаговая схема нужна, потому что Bitmap.Save напрямую в
        // FileStream держит и кодировщик, и файл открытыми одновременно,
        // а нам проще «приготовить» байты и быстро сбросить их на диск.
        // ВАЖНО: методу передают КОПИЮ Bitmap, не показанную в PictureBox
        // (см. обработчик BtnSave_Click) — кодирование PNG из фонового
        // потока на видимом Bitmap конфликтует с перерисовкой GDI+.
        // ------------------------------------------------------------------
        public static async Task SavePngAsync(Bitmap image, string path,
            CancellationToken token)
        {
            // Шаг 1: PNG-кодирование в память (фоновый поток).
            byte[] bytes = await Task.Run(() =>
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    image.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }, token);

            // Шаг 2: запись готовых байт в файл асинхронным FileStream.
            using (FileStream fs = new FileStream(path, FileMode.Create,
                FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await fs.WriteAsync(bytes, 0, bytes.Length, token);
                await fs.FlushAsync(token); // дожидаемся фактического сброса на диск
            }
        }
    }
}
