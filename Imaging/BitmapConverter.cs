// ============================================================================
// Лабораторная работа 4. Вариант 18. Файл: Imaging/BitmapConverter.cs
//
// ПРЕОБРАЗОВАНИЯ Bitmap <-> byte[]. Единственное место проекта, где
// разрешено работать с пикселями через GDI+ (LockBits / Marshal.Copy).
//
// ПРАВИЛО ПОТОКОБЕЗОПАСНОСТИ (урок отладки, см. главу про GDI+ в отчёте):
// GDI+-объект Bitmap не потокобезопасен. Методы этого класса можно вызывать
// ТОЛЬКО для Bitmap, который не показан в PictureBox и не используется
// другими потоками. Фон конвейера работает исключительно с byte[] —
// поэтому гонки перерисовки с фоновым LockBits исключены архитектурно.
// ============================================================================
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Lab4Variant18.Imaging
{
    // Статический конвертер: состояния не хранит, нужен только сам Bitmap
    // (или байты) на входе и результат на выходе.
    internal static class BitmapConverter
    {
        // ------------------------------------------------------------------
        // BITMAP -> BYTE[]: снимок пикселей в плоский массив формата BGRA
        // (по 4 байта на пиксель: [0]=B, [1]=G, [2]=R, [3]=A — порядок
        // байт, который GDI+ использует для Format32bppArgb).
        // ------------------------------------------------------------------
        public static byte[] ToBytes(Bitmap bmp)
        {
            // Область захвата — всё изображение целиком.
            Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            // LockBits «отдаёт» пиксельные данные под контроль кода и
            // возвращает дескриптор BitmapData с адресом первой строки
            // (Scan0) и величиной Stride (число байт на строку с учётом
            // выравнивания). Формат запроса фиксированный 32bppArgb:
            // GDI+ сам преобразует пиксели любого исходного формата.
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                // Для Format32bppArgb строка уже выровнена на 4 байта,
                // поэтому Stride == Width*4 и весь буфер можно копировать
                // одним непрерывным куском — построчный цикл не нужен.
                byte[] buf = new byte[bmp.Width * bmp.Height * 4];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                return buf;
            }
            finally
            {
                // UnlockBits ОБЯЗАТЕЛЕН даже при ошибке: пока Bitmap остаётся
                // заблокированным, любой другой доступ к нему (включая
                // перерисовку) выбрасывает «Object is currently in use
                // elsewhere». Поэтому релиз стоит именно в finally.
                bmp.UnlockBits(data);
            }
        }

        // ------------------------------------------------------------------
        // BYTE[] -> BITMAP: собирает НОВЫЙ Bitmap 32bppArgb из плоского
        // массива байт BGRA (buf.Length == width * height * 4).
        // Вызывается в конце конвейера в фоновом потоке — это безопасно,
        // потому что создаваемый Bitmap ещё нигде не показан.
        // ------------------------------------------------------------------
        public static Bitmap FromBytes(byte[] buf, int width, int height)
        {
            // Создаём пустой целевой Bitmap с фиксированным 32-битным
            // форматом: ровно 4 байта на пиксель, альфа-канал включён.
            Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            Rectangle rect = new Rectangle(0, 0, width, height);
            BitmapData data = result.LockBits(rect, ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                // Пишем байты маски прямо в пиксельную память.
                // Нулевой байт — синий, первый — зелёный, второй — красный,
                // третий — альфа (в конвейере альфа всегда 255).
                Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            }
            finally
            {
                // Разблокировка в finally — тот же принцип, что и в ToBytes.
                result.UnlockBits(data);
            }
            return result;
        }
    }
}
