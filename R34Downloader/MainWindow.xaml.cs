using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using R34Downloader.Resources;

namespace R34Downloader;

public partial class MainWindow : Window
{
    // ── Состояние ─────────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private bool _running;
    private readonly AppConfig _appConfig;
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _timer;

    // Цвета статуса
    private static readonly string ColGreen  = "#27AE60";
    private static readonly string ColYellow = "#F39C12";
    private static readonly string ColRed    = "#E74C3C";
    private static readonly string ColAccent = "#C0392B";

    public MainWindow()
    {
        InitializeComponent();
        _appConfig = AppConfig.Load();
        ApiKeyBox.Password = _appConfig.ApiKey;
        UserIdBox.Text = _appConfig.UserId;
        ApiKeyBox.PasswordChanged += (_, _) => SaveConfig();
        UserIdBox.TextChanged += (_, _) => SaveConfig();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => TimerLabel.Text = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
    }

    private void SaveConfig()
    {
        _appConfig.ApiKey = ApiKeyBox.Password.Trim();
        _appConfig.UserId = UserIdBox.Text.Trim();
        _appConfig.Save();
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

            if (running)
            {
                _stopwatch.Restart();
                _timer.Start();
            }
            else
            {
                _stopwatch.Stop();
                _timer.Stop();
            }
        });

    // ─── Получение конфига из полей ввода ────────────────────────────────────

    /// <summary>Попытаться получить DownloadConfig. Вернёт null и напишет в лог при ошибке.</summary>
    private DownloadConfig? TryGetConfig()
    {
        var dir = DirBox.Text.Trim();
        if (string.IsNullOrEmpty(dir))
        {
            Log(Strings.ErrNoFolder);
            return null;
        }
        if (!Directory.Exists(dir))
        {
            Log(string.Format(Strings.ErrFolderNotFound, dir));
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
            Log(Strings.ErrAlreadyRunning);
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
            Title = Strings.BrowseFolderTitle,
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
        SetStatus(Strings.StatusChecking, ColYellow);

        var apiKey = ApiKeyBox.Password.Trim();
        var userId = UserIdBox.Text.Trim();
        var ct     = _cts!.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Downloader.CheckAccessAsync(apiKey, userId, Log, ct);
                SetStatus(Strings.StatusCheckDone, ColGreen);
            }
            catch (Exception ex)
            {
                Log(string.Format(Strings.LogErrorPrefix, ex.Message));
                SetStatus(Strings.StatusError, ColRed);
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
            Log(Strings.ErrNoFolder);
            Release();
            return;
        }
        SetButtons(true);
        SetStatus(Strings.StatusIndexing, ColYellow);
        var ct = _cts!.Token;

        _ = Task.Run(() =>
        {
            try
            {
                Log(Strings.LogRebuildHeader);
                var folders = Downloader.ScanFolders(dir);
                if (folders.Count == 0)
                {
                    Log(Strings.LogNoFoldersInDir);
                    SetStatus(Strings.StatusNoFolders, ColRed);
                    return;
                }

                for (int i = 0; i < folders.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    var folder = folders[i];
                    var name   = Path.GetFileName(folder);
                    int count  = Downloader.RebuildIndex(folder);
                    Log(string.Format(Strings.LogFilesInIndex, name, count));
                    SetProgress((double)(i + 1) / folders.Count,
                                string.Format(Strings.LogFoldersProgress, i + 1, folders.Count));
                }

                Log(Strings.LogRebuildDone);
                SetStatus(Strings.StatusIndexDone, ColGreen);
                SetProgress(1.0, "");
            }
            catch (Exception ex)
            {
                Log(string.Format(Strings.LogErrorPrefix, ex.Message));
                SetStatus(Strings.StatusError, ColRed);
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
        SetStatus(Strings.StatusLoading, ColAccent);
        SetProgress(0, "");

        Log(Strings.LogStartHeader);
        Log(string.Format(Strings.LogDirLabel, cfg.BaseDir));
        Log(string.Format(Strings.LogThreadsLabel, cfg.Threads));
        Log(string.Format(Strings.LogDryRunLabel, cfg.DryRun));
        Log(string.Format(Strings.LogSkipLabel, cfg.SkipExisting));
        var auth = (!string.IsNullOrEmpty(cfg.ApiKey) && !string.IsNullOrEmpty(cfg.UserId))
            ? $"user_id={cfg.UserId}" : Strings.LogAuthAnon;
        Log(string.Format(Strings.LogAuthLabel, auth));
        Log(Strings.LogSeparator);

        var ct = _cts!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Downloader.RunAsync(cfg, Log, SetProgress, SetStatus, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log(string.Format(Strings.LogUnexpectedError, ex.Message));
                SetStatus(Strings.StatusError, ColRed);
            }
            finally { Release(); }
        }, CancellationToken.None);
    }

    // ── Стоп ──────────────────────────────────────────────────────────────────

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (!_running || _cts is null) return;
        _cts.Cancel();
        Log(Strings.LogStopSignal);
        SetStatus(Strings.StatusStopping, ColYellow);
    }
}
