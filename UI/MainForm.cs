// ============================================================================
// Лабораторная работа 4. Вариант 18. Файл: UI/MainForm.cs
//
// ГЛАВНОЕ ОКНО (UI-ПОТОК) и асинхронные обработчики кнопок.
// Окно НЕ знает, как устроена обработка: оно только
//   1) берёт у пользователя файл/цвет/допуск;
//   2) снимает пиксели в byte[] НА UI-ПОТОКЕ (безопасно для GDI+);
//   3) запускает ColorMaskProcessor.Process в фоновом потоке через Task.Run;
//   4) показывает прогресс/журнал через Progress<T> (сам маршалит в UI);
//   5) показывает и сохраняет результат.
//
// ЖЕЛЕЗНОЕ ПРАВИЛО ПОТОКОБЕЗОПАСНОСТИ WinForms: с контролами и Bitmap,
// показанными в PictureBox, работает только UI-поток. Фоновые потоки
// общаются с UI только через IProgress<T> (Progress<T> фиксирует контекст
// синхронизации UI при создании и вызывает .Report() безопасно из любого
// потока). Нарушение правила даёт «Object is currently in use elsewhere» —
// см. главу отладки отчёта.
// ============================================================================
using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Lab4Variant18.Core;
using Lab4Variant18.Imaging;

namespace Lab4Variant18.UI
{
    internal class MainForm : Form
    {
        // ------------------------- состояние формы --------------------------
        // Путь к выбранному изображению; null, пока файл не выбран.
        private string _inputPath;
        // Источник сигнала отмены: создаётся на каждый запуск обработки.
        private CancellationTokenSource _cts;
        // Исходное изображение (превью в PictureBox).
        private Bitmap _sourceImage;
        // Маска — результат обработки (показывается и сохраняется).
        private Bitmap _resultImage;
        // Цвет, выбранный пользователем через ColorDialog («Свой цвет...»).
        private Color _customColor = Color.FromArgb(255, 0, 0);

        // ---------------------------- контролы -------------------------------
        // Четыре кнопки рабочего сценария: выбрать файл / обработать /
        // отменить / сохранить результат.
        private Button _btnOpen, _btnStart, _btnCancel, _btnSave;
        // Комбобокс целевого цвета диапазона.
        private ComboBox _cmbColor;
        // Поле допуска цветового расстояния (0..200).
        private NumericUpDown _numTolerance;
        // Полоса прогресса обработки (0..100).
        private ProgressBar _progress;
        // Область показа изображения (превью или маска).
        private PictureBox _picture;
        // Журнал событий конвейера (строки из log.Report).
        private ListBox _log;
        // Подписи: путь к файлу и текущий статус операции.
        private Label _lblFile, _lblStatus;

        // Progress<T> создаётся в UI-потоке и сам маршалит вызовы .Report()
        // обратно в UI, поэтому обновлять прогресс и журнал можно из любых
        // потоков конвейера напрямую — никакой ручной Invoke не нужен.
        private readonly IProgress<int> _uiPercent;
        private readonly IProgress<string> _uiLog;

        public MainForm()
        {
            // Канал прогресса: 0..100, с защитой от выхода за Maximum.
            _uiPercent = new Progress<int>(
                v => _progress.Value = Math.Min(v, _progress.Maximum));
            // Канал журнала: каждая строка добавляется в список на UI-потоке.
            _uiLog = new Progress<string>(s => _log.Items.Add(s));
            // Собираем интерфейс (без дизайнера WinForms — чистый код).
            BuildUi();
        }

        // ------------------------------------------------------------------
        // ПОСТРОЕНИЕ ИНТЕРФЕЙСА КОДОМ: панель кнопок сверху, строка пути,
        // прогресс, статус, журнал снизу и PictureBox на всё остальное место.
        // ------------------------------------------------------------------
        private void BuildUi()
        {
            // Заголовок окна и разумные пределы размеров окна.
            Text = "Лаб. работа 4, вариант 18 — цветовая маска (пул потоков)";
            MinimumSize = new Size(780, 560);
            Size = new Size(940, 660);
            // Окно открывается по центру экрана.
            StartPosition = FormStartPosition.CenterScreen;

            // Верхняя панель: кнопки и параметры в одну строку без переноса.
            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;       // прижать к верхнему краю окна
            top.AutoSize = true;            // высота под содержимое
            top.WrapContents = false;       // не переносить элементы на новую строку
            top.Padding = new Padding(6);   // небольшой отступ внутри панели

            // Кнопка 1: выбор файла (диалог открывается только на UI-потоке).
            _btnOpen = new Button();
            _btnOpen.Text = "1. Выбрать изображение...";
            _btnOpen.AutoSize = true;
            _btnOpen.Click += BtnOpen_Click; // подписка на нажатие

            // Комбобокс целевого цвета: список фиксированный, «Свой цвет...»
            // открывает ColorDialog в обработчике события изменения выбора.
            _cmbColor = new ComboBox();
            _cmbColor.DropDownStyle = ComboBoxStyle.DropDownList; // только выбор
            _cmbColor.Width = 130;
            _cmbColor.Items.AddRange(new object[]
                { "Красный", "Зелёный", "Синий", "Жёлтый", "Свой цвет..." });
            _cmbColor.SelectedIndex = 0;    // по умолчанию «Красный» (вариант 18)
            _cmbColor.SelectedIndexChanged += CmbColor_SelectedIndexChanged;

            // Подпись и поле допуска: 0..200, по умолчанию 80.
            Label lblTol = new Label();
            lblTol.Text = "Допуск:";
            lblTol.AutoSize = true;
            // Выравниваем подпись по вертикали к полю ввода.
            lblTol.Margin = new Padding(9, 6, 3, 0);

            _numTolerance = new NumericUpDown();
            _numTolerance.Minimum = 0;      // 0 — только точное совпадение цвета
            _numTolerance.Maximum = 200;    // 200 — максимально широкий диапазон
            _numTolerance.Value = 80;       // стартовое значение допуска
            _numTolerance.Width = 56;

            // Кнопки 2–4: обработка / отмена / сохранение. Пока нет файла —
            // «Обработать» недоступна; «Отмена» и «Сохранить» включаются
            // только на соответствующем этапе (см. SetBusy ниже).
            _btnStart = new Button();
            _btnStart.Text = "2. Обработать";
            _btnStart.AutoSize = true;
            _btnStart.Enabled = false;
            _btnStart.Click += BtnStart_Click;

            _btnCancel = new Button();
            _btnCancel.Text = "3. Отмена";
            _btnCancel.AutoSize = true;
            _btnCancel.Enabled = false;
            _btnCancel.Click += BtnCancel_Click;

            _btnSave = new Button();
            _btnSave.Text = "4. Сохранить результат...";
            _btnSave.AutoSize = true;
            _btnSave.Enabled = false;
            _btnSave.Click += BtnSave_Click;

            // Складываем все элементы верхней панели в нужном порядке.
            top.Controls.AddRange(new Control[]
                { _btnOpen, _cmbColor, lblTol, _numTolerance, _btnStart, _btnCancel, _btnSave });

            // Строка «путь к файлу» под панелью кнопок.
            _lblFile = new Label();
            _lblFile.Dock = DockStyle.Top;
            _lblFile.Height = 22;
            _lblFile.TextAlign = ContentAlignment.MiddleLeft; // текст слева
            _lblFile.Padding = new Padding(8, 2, 0, 0);
            _lblFile.Text = "Файл не выбран";

            // Полоса прогресса 0..100 (обновляется из конвейера через _uiPercent).
            _progress = new ProgressBar();
            _progress.Dock = DockStyle.Top;
            _progress.Height = 16;

            // Строка статуса: что сейчас происходит с программой.
            _lblStatus = new Label();
            _lblStatus.Dock = DockStyle.Top;
            _lblStatus.Height = 22;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Padding = new Padding(8, 2, 0, 0);
            _lblStatus.Text = "Готово к работе";

            // Панель журнала внизу окна: ListBox на весь её размер.
            Panel logPanel = new Panel();
            logPanel.Dock = DockStyle.Bottom;   // прижать к нижнему краю
            logPanel.Height = 120;              // фиксированная высота журнала
            _log = new ListBox();
            _log.Dock = DockStyle.Fill;         // заполнить всю панель
            // IntegralHeight=false: точная высота списка, без подгонки
            // под целое число элементов (иначе лишние пиксели снизу).
            _log.IntegralHeight = false;
            logPanel.Controls.Add(_log);

            // Область изображения: занимает всё, что осталось, с масштабом
            // «вписать в окно» (Zoom) и светлым фоном вокруг картинки.
            _picture = new PictureBox();
            _picture.Dock = DockStyle.Fill;
            _picture.SizeMode = PictureBoxSizeMode.Zoom;
            _picture.BackColor = Color.FromArgb(240, 240, 240);

            // Порядок добавления важен: Dock-разметка выполняется с КОНЦА
            // списка, поэтому Fill-элемент добавлен первым — он займёт
            // оставшееся место, а остальные прижмутся к своим краям.
            Controls.Add(_picture);
            Controls.Add(logPanel);
            Controls.Add(_lblStatus);
            Controls.Add(_progress);
            Controls.Add(_lblFile);
            Controls.Add(top);
        }

        // ----------------------- обработчики кнопок -------------------------

        // Кнопка 1: выбрать файл и сразу показать быстрое превью.
        private void BtnOpen_Click(object sender, EventArgs e)
        {
            // Диалог в using: закрывается и освобождается сам.
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                // Фильтр: только картинки плюс вариант «все файлы».
                dlg.Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Все файлы|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _inputPath = dlg.FileName; // запоминаем выбранный путь
                try
                {
                    Bitmap preview = new Bitmap(_inputPath); // быстрое превью
                    DisposeImage(ref _sourceImage);          // убрать прежнее превью
                    _sourceImage = preview;
                    DisposeImage(ref _resultImage);          // прежняя маска неактуальна
                    _picture.Image = _sourceImage;           // показываем превью
                    _lblFile.Text = _inputPath;              // путь в подпись
                    _lblStatus.Text = "Файл выбран. Нажмите \"Обработать\".";
                    _btnStart.Enabled = true;                // разрешаем обработку
                }
                catch (Exception ex)
                {
                    // Не картинка или файл занят: откатываем выбор.
                    _inputPath = null;
                    _btnStart.Enabled = false;
                    MessageBox.Show("Не удалось открыть изображение: " + ex.Message);
                }
            }
        }

        // Выбор «Свой цвет...» открывает системный диалог выбора цвета.
        private void CmbColor_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Пункт 4 комбобокса — «Свой цвет...».
            if (_cmbColor.SelectedIndex == 4)
            {
                using (ColorDialog dlg = new ColorDialog())
                {
                    dlg.Color = _customColor; // стартовый цвет — прежний выбор
                    dlg.FullOpen = true;      // показать поля ручного ввода
                    if (dlg.ShowDialog(this) == DialogResult.OK) _customColor = dlg.Color;
                }
            }
        }

        // Целевой цвет по выбранному пункту комбобокса (вариант 18 исторически
        // начинается с «только красные» — пункт 0).
        private Color SelectedColor()
        {
            switch (_cmbColor.SelectedIndex)
            {
                case 1: return Color.FromArgb(0, 255, 0);   // зелёный
                case 2: return Color.FromArgb(0, 0, 255);   // синий
                case 3: return Color.FromArgb(255, 255, 0); // жёлтый
                case 4: return _customColor;                // цвет из ColorDialog
                default: return Color.FromArgb(255, 0, 0);  // «только красные»
            }
        }

        // Кнопка 3: кооперативная отмена конвейера — ставит флаг в токене,
        // все части конвейера проверяют его в удобных точках.
        private void BtnCancel_Click(object sender, EventArgs e)
        {
            if (_cts != null) _cts.Cancel();
        }

        // ------------------------ главный конвейер --------------------------

        // Кнопка 2: обработка. async void — корректно ТОЛЬКО для обработчиков
        // событий: исключения здесь ловятся try/catch, UI не блокируется
        // ни на одном await.
        private async void BtnStart_Click(object sender, EventArgs e)
        {
            if (_inputPath == null) return; // без файла обрабатывать нечего
            SetBusy(true);                  // блокируем кнопки на время работы
            _log.Items.Clear();             // новый прогон — новый журнал
            Stopwatch clock = Stopwatch.StartNew(); // замер полного времени обработки
            // Новый источник отмены на каждый запуск: предыдущий уже закрыт
            // в finally предыдущего прогона.
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            try
            {
                // --- ЭТАП 1: асинхронное чтение файла (UI не блокируется) ---
                _lblStatus.Text = "Чтение файла...";
                Bitmap source = await ImageFileIO.LoadImageAsync(_inputPath, token);

                // Снимок пикселей делаем НА UI-ПОТОКЕ (ещё ДО того, как битмап
                // попадёт в PictureBox). Bitmap, показанный в PictureBox, из
                // фоновых потоков трогать нельзя: GDI+ не потокобезопасен,
                // перерисовка во время LockBits из другого потока даёт
                // «Object is currently in use elsewhere» (см. главу отладки).
                byte[] srcBytes = BitmapConverter.ToBytes(source);
                int width = source.Width;
                int height = source.Height;

                DisposeImage(ref _sourceImage);
                _sourceImage = source;
                _picture.Image = _sourceImage; // превью снова в PictureBox
                Log(string.Format("Загружено {0}x{1}", width, height));

                // --- ЭТАП 2: многопоточная обработка в фоновом потоке ---
                // Значения контролов читаем ТОЛЬКО на UI-потоке и передаём
                // в фоновую задачу готовыми параметрами (читать контролы
                // из другого потока — та же ошибка, что и рисовать).
                _lblStatus.Text = "Обработка...";
                Color target = SelectedColor();
                int tolerance = (int)_numTolerance.Value;
                // Task.Run уводит конвейер в фоновый поток; await возвращает
                // нас на UI-поток, когда маска готова.
                Bitmap result = await Task.Run(
                    () => ColorMaskProcessor.Process(srcBytes, width, height,
                        target, tolerance, token, _uiPercent, _uiLog), token);

                // --- Готово: показываем маску и разрешаем сохранение ---
                DisposeImage(ref _resultImage);
                _resultImage = result;
                _picture.Image = _resultImage; // маска вместо превью
                clock.Stop();
                _lblStatus.Text = string.Format(
                    "Готово за {0} мс. Нажмите \"Сохранить результат\".",
                    clock.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                // Отмена: частичный результат не показывается и не сохраняется.
                DisposeImage(ref _resultImage);
                _picture.Image = _sourceImage; // возвращаем превью
                _lblStatus.Text = "Обработка отменена. Результат не сохранён.";
            }
            catch (Exception ex)
            {
                // Любая другая ошибка — показываем текст и оставляем превью.
                MessageBox.Show("Ошибка: " + ex.Message);
                _lblStatus.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                SetBusy(false); // в любом случае возвращаем кнопки
                if (_cts != null) { _cts.Dispose(); _cts = null; }
            }
        }

        // ----------------------- сохранение результата -----------------------

        // Кнопка 4: сохранение маски в PNG. Копия для кодирования создаётся
        // НА UI-ПОТОКЕ: _resultImage показан в PictureBox, и PNG-кодирование
        // того же Bitmap из фонового потока конфликтует с перерисовкой —
        // та же ошибка GDI+, тот самый catch «Не удалось сохранить».
        private async void BtnSave_Click(object sender, EventArgs e)
        {
            if (_resultImage == null) return; // сохранять ещё нечего
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG-изображение|*.png";
                dlg.FileName = "mask.png"; // имя по умолчанию
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                _btnSave.Enabled = false;       // одна операция сохранения за раз
                _lblStatus.Text = "Сохранение...";
                Bitmap snapshot = new Bitmap(_resultImage); // копия на UI-потоке
                try
                {
                    // --- ЭТАП 3: асинхронная запись файла результата ---
                    await ImageFileIO.SavePngAsync(snapshot, dlg.FileName,
                        CancellationToken.None);
                    _lblStatus.Text = "Сохранено: " + dlg.FileName;
                    Log("Сохранено: " + dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось сохранить: " + ex.Message);
                    _lblStatus.Text = "Ошибка сохранения";
                }
                finally
                {
                    // Копия больше не нужна в любом исходе (успех/ошибка).
                    snapshot.Dispose();
                    _btnSave.Enabled = true;
                }
            }
        }

        // ---------------------------- вспомогательные ------------------------

        // Строка журнала. Безопасно из любого потока: Progress<string>
        // сам переносит добавление в UI-поток.
        private void Log(string message)
        {
            _uiLog.Report(message);
        }

        // Управление доступностью кнопок по текущему этапу работы:
        // busy=true — идёт чтение/обработка/отмена (доступна только «Отмена»);
        // busy=false — простой («Обработать» — если есть файл, «Сохранить» —
        // если есть результат).
        private void SetBusy(bool busy)
        {
            _btnOpen.Enabled = !busy;                        // выбор файла — только в простое
            _btnStart.Enabled = !busy && _inputPath != null; // обработка: нужен файл
            _btnCancel.Enabled = busy;                       // отмена — только во время работы
            _btnSave.Enabled = !busy && _resultImage != null; // сохранение: нужен результат
        }

        // Освобождение Bitmap с обнулением ссылки (у GDI+-объектов
        // Dispose обязателен — это неуправляемый ресурс ОС).
        private static void DisposeImage(ref Bitmap img)
        {
            if (img != null)
            {
                Bitmap old = img;
                img = null;      // сначала убираем ссылку, потом Dispose:
                old.Dispose();   // исключаем повторное освобождение
            }
        }

        // Закрытие окна: освобождаем обе картинки, чтобы не оставлять
        // неуправляемые ресурсы GDI+ «на милость» сборщика мусора.
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            DisposeImage(ref _sourceImage);
            DisposeImage(ref _resultImage);
        }
    }
}
