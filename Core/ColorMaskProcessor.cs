// ============================================================================

//
// ЯДРО КОНВЕЙЕРА: планировщик -> пул потоков -> сборщик результатов.
// Выполняется целиком в ФОНОВОМ потоке (его запускает Task.Run из MainForm),
// поэтому отсюда запрещено трогать любые контролы и Bitmap.
//
// Здесь живут остальные ДВА механизма синхронизации из пяти заявленных:
//   4) SemaphoreSlim  — «пропуска» в зону обработки: одновременно считать
//                       маску может не больше permits полос;
//   5) CountdownEvent — счётчик полос: главный поток ждёт, пока каждая
//                       полоса не вызовет Signal(); ноль = всё готово.
// Плюс lock (Monitor) — критическая секция для очереди результатов,
// счётчика прогресса и журнала (общие данные, которые трогают все потоки).
//
// ВАЖНО (урок отладки): в этот класс НЕ передаётся ни один Bitmap — только
// byte[] пикселей, снятый на UI-потоке. GDI+ не потокобезопасен, поэтому
// фоновая часть работает исключительно с массивами байт.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using Lab4Variant18.Imaging;
using Lab4Variant18.Threading;

namespace Lab4Variant18.Core
{
    // Статический процессор: состояние создаётся внутри Process на один
    // запуск конвейера и умирает вместе с ним — «утечек» потоков и событий
    // между запусками нет.
    internal static class ColorMaskProcessor
    {
        // ------------------------------------------------------------------
        // ГЛАВНЫЙ МЕТОД КОНВЕЙЕРА: возвращает маску как новый Bitmap.
        // src          — снимок пикселей источника (BGRA, width*height*4);
        // target       — целевой цвет диапазона (выбран в UI);
        // tolerance    — допуск цветового расстояния (0..200);
        // token        — сигнал кооперативной отмены;
        // percent/log  — каналы прогресса и журнала в UI (Progress<T> сам
        //                маршалит вызовы .Report() в UI-поток).
        // ------------------------------------------------------------------
        public static Bitmap Process(byte[] src, int width, int height,
            Color target, int tolerance, CancellationToken token,
            IProgress<int> percent, IProgress<string> log)
        {
            // Общий буфер результата: маска в том же формате BGRA.
            // Собирается из полос только ПОСЛЕ завершения всех потоков.
            byte[] dst = new byte[width * height * 4];

            // ---- настройка параллелизма под конкретную машину ----
            int cores = Math.Max(1, Environment.ProcessorCount);
            int permits = Math.Min(cores, 8);  // пропусков у семафора — не больше 8
            int poolSize = permits * 2;        // потоков вдвое больше: часть из них
                                               // стоит в очереди на семафор, и это
                                               // демонстрирует его работу
            int stripCount = StripPlanner.ChooseStripCount(height, cores);
            int tol2 = tolerance * tolerance;  // сравниваем квадраты расстояний

            // Держим ссылки, чтобы закрыть их в finally даже при исключении.
            WorkerPool pool = null;
            CountdownEvent allDone = null;
            SemaphoreSlim sem = null;
            try
            {
                pool = new WorkerPool(poolSize);           // создали потоки
                allDone = new CountdownEvent(stripCount);  // счётчик = числу полос
                sem = new SemaphoreSlim(permits);          // стартовые «пропуска»

                // Общие данные, защищаемые lock (их трогают все рабочие потоки):
                object gate = new object();  // монитор критической секции
                int done = 0;                // счётчик готовых полос
                Queue<StripResult> results = new Queue<StripResult>(); // результаты
                List<string> journal = new List<string>();             // журнал полос

                log.Report(string.Format("Пул: {0} потоков, семафор: {1}, полос: {2}",
                    poolSize, permits, stripCount));

                // ---- ПЛАНИРОВЩИК: нарезаем изображение на полосы и
                //      складываем по одной задаче в пул на каждую полосу ----
                List<StripPlan> plans = StripPlanner.Build(height, stripCount);

                foreach (StripPlan plan in plans)
                {
                    // Локальная копия для замыкания: гарантируем, что задача
                    // увидит ИМЕННО свою полосу, а не переменную цикла.
                    StripPlan p = plan;

                    pool.Submit(() =>
                    {
                        try
                        {
                            byte[] slice = null;  // результат полосы
                            string error = null;  // текст ошибки, если полоса упала
                            try
                            {
                                sem.Wait();       // берём «пропуск» семафора;
                                                  // если пропусков нет — поток
                                                  // ждёт, не занимая CPU
                                try
                                {
                                    // Отменённые полосы не считаем: отмена
                                    // кооперативная — проверяем флаг сами.
                                    if (!token.IsCancellationRequested)
                                        slice = ProcessStrip(src, width, p.Y0, p.Y1,
                                            target, tol2, token);
                                }
                                finally { sem.Release(); } // «пропуск» возвращается
                                                           // ВСЕГДА, даже при ошибке
                            }
                            catch (Exception ex) { error = ex.ToString(); }
                            // Ошибку пишем в журнал, а НЕ показываем MessageBox:
                            // у потока пула нет цикла сообщений, MessageBox из
                            // него недопустим.

                            // ====== КРИТИЧЕСКАЯ СЕКЦИЯ (Monitor / lock) ======
                            // Защищает общие данные: очередь результатов,
                            // счётчик прогресса и журнал. Внутри lock в любой
                            // момент находится не более одного потока, поэтому
                            // done/queue/journal не портятся гонками.
                            lock (gate)
                            {
                                done++;
                                if (error != null)
                                    journal.Add(string.Format(
                                        "{0}: полоса #{1} — ОШИБКА: {2}",
                                        Thread.CurrentThread.Name, p.Index, error));
                                else if (slice == null)
                                    journal.Add(string.Format(
                                        "{0}: полоса #{1} пропущена (отмена)",
                                        Thread.CurrentThread.Name, p.Index));
                                else
                                {
                                    // Кладём результат и строку журнала —
                                    // только под lock, вместе со счётчиком.
                                    results.Enqueue(new StripResult(p.Y0, p.Y1, slice));
                                    journal.Add(string.Format(
                                        "{0}: полоса #{1} (строки {2}-{3}) готова, {4}/{5}",
                                        Thread.CurrentThread.Name, p.Index, p.Y0, p.Y1 - 1,
                                        done, stripCount));
                                }
                                percent.Report(done * 100 / stripCount);
                            }
                            // ============== КОНЕЦ КРИТИЧЕСКОЙ СЕКЦИИ ==============
                        }
                        finally
                        {
                            allDone.Signal(); // полоса завершена (и при отмене,
                                              // и при ошибке) — счётчик уменьшен
                        }
                    });
                }

                // ---- ГЛАВНЫЙ ПОТОК КОНВЕЙЕРА ЖДЁТ: завершение ВСЕХ полос
                //      ИЛИ сигнал отмены (что наступит раньше) ----
                int which = WaitHandle.WaitAny(new WaitHandle[]
                    { allDone.WaitHandle, token.WaitHandle });
                bool canceled = (which == 1);

                if (canceled)
                    log.Report("Отмена: очищаем очередь заданий и останавливаем пул...");

                // Останавливаем пул: очищает очередь непринятых заданий,
                // будит все потоки (ManualResetEvent) и Join-ом дожидается их.
                pool.Shutdown();

                if (!canceled)
                {
                    // ---- СБОРЩИК РЕЗУЛЬТАТОВ: переливаем готовые полосы из
                    //      очереди в общий буфер по их собственным границам.
                    //      lock уже не обязателен (все потоки завершились),
                    //      но оставлен единообразия ради и на случай расширения.
                    lock (gate)
                    {
                        while (results.Count > 0)
                        {
                            StripResult r = results.Dequeue();
                            // dst + смещение первой строки полосы, длина ровно
                            // по факту полосы: никаких «одинаковых высот».
                            Buffer.BlockCopy(r.Pixels, 0, dst,
                                r.Y0 * width * 4, r.Pixels.Length);
                        }
                    }
                }

                // Журнал полос выводим после остановки пула — в порядке,
                // собранном под lock (строки добавлялись по мере готовности).
                foreach (string line in journal) log.Report(line);

                if (canceled) throw new OperationCanceledException(token);

                // Собираем Bitmap маски из общего буфера. Безопасно: этот
                // Bitmap ещё нигде не показан, LockBits завершён до возврата.
                return BitmapConverter.FromBytes(dst, width, height);
            }
            finally
            {
                // pool.Shutdown() идемпотентен: даже если выше возникло
                // исключение, мы дожидаемся всех потоков и только затем
                // освобождаем события — безопасный порядок разрушения.
                if (pool != null) pool.Shutdown();
                if (allDone != null) allDone.Dispose();
                if (sem != null) sem.Dispose();
                if (pool != null) pool.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // АЛГОРИТМ ВАРИАНТА 18: обработка одной полосы строк.
        //
        // Пиксель попадает в маску, если евклидово расстояние от его цвета
        // до целевого цвета не превышает допуск:
        //     (R-cR)^2 + (G-cG)^2 + (B-cB)^2 <= tolerance^2.
        // Корень извлекать незачем: сравниваем квадраты расстояний (tol2).
        // Пиксели внутри диапазона становятся белыми (255), остальные —
        // чёрными (0), альфа-канал всегда 255 (непрозрачная маска).
        //
        // ПОТОКОБЕЗОПАСНОСТЬ: метод читает общий буфер-источник (read-only)
        // и пишет ТОЛЬКО в свой локальный массив slice — общие данные он не
        // изменяет, поэтому lock ему не нужен.
        // ------------------------------------------------------------------
        private static byte[] ProcessStrip(byte[] src, int width, int y0, int y1,
            Color target, int tol2, CancellationToken token)
        {
            // Компоненты целевого цвета — вынесены из цикла ради скорости.
            int tr = target.R, tg = target.G, tb = target.B;
            // Локальный результат полосы: ровно (y1 - y0) строк по width пикселей.
            byte[] slice = new byte[(y1 - y0) * width * 4];
            int o = 0; // курсор записи в slice (в байтах)

            for (int y = y0; y < y1; y++)           // строки своей полосы
            {
                if (token.IsCancellationRequested) return null; // отмена в полосе
                int row = y * width * 4;            // смещение начала строки в src
                for (int x = 0; x < width; x++)     // пиксели строки слева направо
                {
                    int i = row + x * 4;            // GDI+ хранит байты в порядке
                                                    // B,G,R,A — отсюда смещения ниже
                    int db = src[i]     - tb;       // разница по синему каналу
                    int dg = src[i + 1] - tg;       // разница по зелёному каналу
                    int dr = src[i + 2] - tr;       // разница по красному каналу
                    byte v = (dr * dr + dg * dg + db * db <= tol2)
                        ? (byte)255 : (byte)0;      // белый — «в диапазоне»,
                                                    // чёрный — «вне диапазона»
                    slice[o]     = v;               // B
                    slice[o + 1] = v;               // G
                    slice[o + 2] = v;               // R (все три равны — маска
                                                    // получается ч/б изображением)
                    slice[o + 3] = 255;             // A — полностью непрозрачно
                    o += 4;
                }
            }
            return slice;
        }
    }
}
