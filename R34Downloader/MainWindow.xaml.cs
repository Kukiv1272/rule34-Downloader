using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace R34Downloader;

public partial class MainWindow : Window
{
    // ── Состояние ─────────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private bool _running;

    // Цвета статуса
    private static readonly string ColGreen  = "#27AE60";
    private static readonly string ColYellow = "#F39C12";
    private static readonly string ColRed    = "#E74C3C";
    private static readonly string ColAccent = "#C0392B";

    public MainWindow()
    {
        InitializeComponent();
    }

    // ─── Вспомогательные методы UI ───────────────────────────────────────────

    /// <summary>Записать строку в лог (можно вызывать из любого потока).</summary>
    private void Log(string msg) =>
        Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText(msg + "\n");
            LogBox.ScrollToEnd();
        });

    /// <summary>Обновить статус в шапке (можно вызывать из любого потока).</summary>
    private void SetStatus(string text, string colorHex) =>
        Dispatcher.InvokeAsync(() =>
        {
            var brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(colorHex));
            StatusLabel.Text       = text;
            StatusLabel.Foreground = brush;
            StatusDot.Foreground   = brush;
        });

    /// <summary>Обновить прогресс-бар (можно вызывать из любого потока).</summary>
    private void SetProgress(double value, string label) =>
        Dispatcher.InvokeAsync(() =>
        {
            ProgressBar.Value    = Math.Clamp(value, 0, 1);
            ProgressLabel.Text   = label;
        });

    /// <summary>Переключить доступность кнопок.</summary>
    private void SetButtons(bool running) =>
        Dispatcher.InvokeAsync(() =>
        {
            _running                = running;
            BtnStart.IsEnabled      = !running;
            BtnCheckAccess.IsEnabled = !running;
            BtnRebuildIndex.IsEnabled = !running;
            BtnStop.IsEnabled       = running;
        });

    // ─── Получение конфига из полей ввода ────────────────────────────────────

    /// <summary>Попытаться получить DownloadConfig. Вернёт null и напишет в лог при ошибке.</summary>
    private DownloadConfig? TryGetConfig()
    {
        var dir = DirBox.Text.Trim();
        if (string.IsNullOrEmpty(dir))
        {
            Log("! Укажите папку с тегами");
            return null;
        }
        if (!Directory.Exists(dir))
        {
            Log($"! Папка не найдена: {dir}");
            return null;
        }

        return new DownloadConfig(
            BaseDir:      dir,
            Threads:      (int)ThreadsSlider.Value,
            DryRun:       DryRunCheck.IsChecked == true,
            SkipExisting: SkipExistingCheck.IsChecked == true,
            ApiKey:       ApiKeyBox.Password.Trim(),
            UserId:       UserIdBox.Text.Trim()
        );
    }

    private bool Acquire()
    {
        if (_running)
        {
            Log("! Уже выполняется операция");
            return false;
        }
        _cts = new CancellationTokenSource();
        return true;
    }

    private void Release()
    {
        _cts?.Dispose();
        _cts = null;
        SetButtons(false);
    }

    // ─── Обработчики кнопок ──────────────────────────────────────────────────

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        // OpenFolderDialog доступен в .NET 8 WPF без WinForms
        var dlg = new OpenFolderDialog
        {
            Title = "Выберите папку с тегами",
        };
        if (!string.IsNullOrEmpty(DirBox.Text) && Directory.Exists(DirBox.Text))
            dlg.InitialDirectory = DirBox.Text;

        if (dlg.ShowDialog(this) == true)
            DirBox.Text = dlg.FolderName;
    }

    private void ThreadsSlider_ValueChanged(object sender,
        System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThreadsLabel is null) return;
        ThreadsLabel.Text = ((int)e.NewValue).ToString();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) =>
        LogBox.Clear();

    // ── Проверка доступа ──────────────────────────────────────────────────────

    private void BtnCheckAccess_Click(object sender, RoutedEventArgs e)
    {
        if (!Acquire()) return;
        SetButtons(true);
        SetStatus("проверка...", ColYellow);

        var apiKey = ApiKeyBox.Password.Trim();
        var userId = UserIdBox.Text.Trim();
        var ct     = _cts!.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Downloader.CheckAccessAsync(apiKey, userId, Log, ct);
                SetStatus("проверка завершена", ColGreen);
            }
            catch (Exception ex)
            {
                Log($"! Ошибка: {ex.Message}");
                SetStatus("ошибка", ColRed);
            }
            finally { Release(); }
        }, ct);
    }

    // ── Пересоздание индексов ─────────────────────────────────────────────────

    private void BtnRebuildIndex_Click(object sender, RoutedEventArgs e)
    {
        if (!Acquire()) return;
        var dir = DirBox.Text.Trim();
        if (string.IsNullOrEmpty(dir))
        {
            Log("! Укажите папку с тегами");
            Release();
            return;
        }
        SetButtons(true);
        SetStatus("индексация...", ColYellow);
        var ct = _cts!.Token;

        _ = Task.Run(() =>
        {
            try
            {
                Log("── Пересоздание индексов ────────────────────");
                var folders = Downloader.ScanFolders(dir);
                if (folders.Count == 0)
                {
                    Log("  Нет папок в директории.");
                    SetStatus("нет папок", ColRed);
                    return;
                }

                for (int i = 0; i < folders.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    var folder = folders[i];
                    var name   = Path.GetFileName(folder);
                    int count  = Downloader.RebuildIndex(folder);
                    Log($"  {name}: {count} файлов в индексе");
                    SetProgress((double)(i + 1) / folders.Count,
                                $"{i + 1}/{folders.Count} папок");
                }

                Log("── Готово ───────────────────────────────────");
                SetStatus("индексация завершена", ColGreen);
                SetProgress(1.0, "");
            }
            catch (Exception ex)
            {
                Log($"! Ошибка: {ex.Message}");
                SetStatus("ошибка", ColRed);
            }
            finally { Release(); }
        }, ct);
    }

    // ── Основная загрузка ─────────────────────────────────────────────────────

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (!Acquire()) return;
        var cfg = TryGetConfig();
        if (cfg is null) { Release(); return; }

        SetButtons(true);
        SetStatus("загрузка...", ColAccent);
        SetProgress(0, "");

        Log("── Запуск ───────────────────────────────────");
        Log($"  Директория : {cfg.BaseDir}");
        Log($"  Потоки     : {cfg.Threads}");
        Log($"  Dry-run    : {cfg.DryRun}");
        Log($"  Пропускать : {cfg.SkipExisting}");
        var auth = (!string.IsNullOrEmpty(cfg.ApiKey) && !string.IsNullOrEmpty(cfg.UserId))
            ? $"user_id={cfg.UserId}" : "анонимно";
        Log($"  Авторизация: {auth}");
        Log("─────────────────────────────────────────────");

        var ct = _cts!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Downloader.RunAsync(cfg, Log, SetProgress, SetStatus, ct);
            }
            catch (OperationCanceledException) { /* уже обработано внутри RunAsync */ }
            catch (Exception ex)
            {
                Log($"! Неожиданная ошибка: {ex.Message}");
                SetStatus("ошибка", ColRed);
            }
            finally { Release(); }
        }, CancellationToken.None);  // Task.Run сам не должен отменяться, логика внутри
    }

    // ── Стоп ──────────────────────────────────────────────────────────────────

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (!_running || _cts is null) return;
        _cts.Cancel();
        Log("  >> Отправлен сигнал остановки...");
        SetStatus("остановка...", ColYellow);
    }
}
