using System.IO;
using System.Net;
using System.Net.Http;
using System.Xml.Linq;

namespace R34Downloader;

// ─── Модели ───────────────────────────────────────────────────────────────────

public record Post(string Id, string FileUrl, string Ext);

public enum DownloadStatus { Ok, Skip, Fail }

public record DownloadResult(string PostId, DownloadStatus Status, string Ext);

// ─── Конфигурация сессии ──────────────────────────────────────────────────────

public record DownloadConfig(
    string BaseDir,
    int    Threads,
    bool   DryRun,
    bool   SkipExisting,
    string ApiKey,
    string UserId
);

// ─── Основной движок ──────────────────────────────────────────────────────────

public class Downloader
{
    // ── Константы ─────────────────────────────────────────────────────────────
    private const string ApiUrl     = "https://api.rule34.xxx/index.php";
    private const int    PageSize   = 100;
    private const double ApiDelaySec = 1.1;
    private const int    MaxRetries  = 5;
    private const int    RetryWaitSec = 15;
    private const string IndexFile  = "downloaded.txt";

    private static readonly string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    private static readonly Dictionary<string, string> ExtMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".gif"]  = "Gif",
        [".webm"] = "Video",
        [".mp4"]  = "Video",
        [".png"]  = "Images",
        [".jpg"]  = "Images",
        [".jpeg"] = "Images",
        [".webp"] = "Images",
    };

    // ── Вспомогательные методы ────────────────────────────────────────────────

    public static string GetSubfolder(string ext) =>
        ExtMap.TryGetValue(ext, out var sub) ? sub : "Images";

    public static HashSet<string> LoadIndex(string artistDir)
    {
        var path = Path.Combine(artistDir, IndexFile);
        if (!File.Exists(path)) return [];
        try
        {
            return File.ReadAllLines(path)
                       .Where(l => !string.IsNullOrWhiteSpace(l))
                       .ToHashSet();
        }
        catch { return []; }
    }

    public static void SaveIndexEntry(string postId, string artistDir, object lockObj)
    {
        var path = Path.Combine(artistDir, IndexFile);
        lock (lockObj)
        {
            File.AppendAllText(path, postId + "\n");
        }
    }

    public static int RebuildIndex(string artistDir)
    {
        var ids = new HashSet<string>();
        foreach (var sub in new[] { "Images", "Gif", "Video" })
        {
            var subDir = Path.Combine(artistDir, sub);
            if (!Directory.Exists(subDir)) continue;
            foreach (var f in Directory.EnumerateFiles(subDir))
                ids.Add(Path.GetFileNameWithoutExtension(f));
        }

        var indexPath = Path.Combine(artistDir, IndexFile);
        try
        {
            var sorted = ids
                .OrderBy(id => long.TryParse(id, out var n) ? n : long.MaxValue)
                .ThenBy(id => id);
            File.WriteAllText(indexPath, string.Join("\n", sorted) + "\n");
        }
        catch { /* ignore */ }

        return ids.Count;
    }

    public static List<string> ScanFolders(string baseDir) =>
        Directory.EnumerateDirectories(baseDir)
                 .Where(d => !Path.GetFileName(d).StartsWith('.'))
                 .OrderBy(d => d)
                 .ToList();

    // ── HTTP-клиент ───────────────────────────────────────────────────────────

    private static HttpClient BuildClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxConnectionsPerServer = 32,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    // ── API-запрос с retry ────────────────────────────────────────────────────

    private static async Task<string?> ApiGetAsync(
        HttpClient client,
        Dictionary<string, string> queryParams,
        string apiKey, string userId,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(userId))
        {
            queryParams["api_key"] = apiKey;
            queryParams["user_id"] = userId;
        }

        var query = string.Join("&", queryParams.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{ApiUrl}?{query}";

        var waitSec = RetryWaitSec;
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            if (ct.IsCancellationRequested) return null;
            try
            {
                var resp = await client.GetAsync(url, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(TimeSpan.FromSeconds(waitSec), ct);
                    waitSec *= 2;
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                await Task.Delay(TimeSpan.FromSeconds(ApiDelaySec), ct);
                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
        }
        return null;
    }

    // ── Получение страницы постов ─────────────────────────────────────────────

    private static async Task<List<Post>> FetchPageAsync(
        HttpClient client,
        string tag, int pid,
        string apiKey, string userId,
        CancellationToken ct)
    {
        var p = new Dictionary<string, string>
        {
            ["page"]  = "dapi",
            ["s"]     = "post",
            ["q"]     = "index",
            ["tags"]  = tag,
            ["limit"] = PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["pid"]   = pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var xml = await ApiGetAsync(client, p, apiKey, userId, ct);
        if (xml is null) return [];

        try
        {
            var doc   = XDocument.Parse(xml);
            var posts = new List<Post>();
            foreach (var el in doc.Root!.Elements("post"))
            {
                var fileUrl = ((string?)el.Attribute("file_url") ?? "").Trim();
                if (string.IsNullOrEmpty(fileUrl)) continue;
                if (fileUrl.StartsWith("//")) fileUrl = "https:" + fileUrl;

                var ext = Path.GetExtension(fileUrl.Split('?')[0]).ToLowerInvariant();
                posts.Add(new Post(
                    (string?)el.Attribute("id") ?? "0",
                    fileUrl,
                    ext
                ));
            }
            return posts;
        }
        catch { return []; }
    }

    // ── Сбор всех постов по тегу ──────────────────────────────────────────────

    private static async Task<List<Post>> CollectAllPostsAsync(
        HttpClient client,
        string tag,
        string apiKey, string userId,
        CancellationToken ct)
    {
        var all = new List<Post>();
        int pid = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await FetchPageAsync(client, tag, pid, apiKey, userId, ct);
            if (batch.Count == 0) break;
            all.AddRange(batch);
            if (batch.Count < PageSize) break;
            pid++;
        }
        return all;
    }

    // ── Скачивание одного файла ───────────────────────────────────────────────

    private static async Task<DownloadResult> DownloadFileAsync(
        HttpClient client,
        Post post,
        string artistDir,
        HashSet<string> index,
        object indexLock,
        bool skipExisting,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return new(post.Id, DownloadStatus.Fail, post.Ext);

        if (skipExisting && index.Contains(post.Id))
            return new(post.Id, DownloadStatus.Skip, post.Ext);

        var subfolder = GetSubfolder(post.Ext);
        var destDir   = Path.Combine(artistDir, subfolder);
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, $"{post.Id}{post.Ext}");

        try
        {
            using var resp = await client.GetAsync(post.FileUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using var file   = File.Create(dest);
            var buf = new byte[65536];
            int read;
            while (!ct.IsCancellationRequested &&
                   (read = await stream.ReadAsync(buf, ct)) > 0)
                await file.WriteAsync(buf.AsMemory(0, read), ct);

            if (ct.IsCancellationRequested)
            {
                file.Close();
                try { File.Delete(dest); } catch { /* ignore */ }
                return new(post.Id, DownloadStatus.Fail, post.Ext);
            }

            lock (indexLock)
                index.Add(post.Id);
            SaveIndexEntry(post.Id, artistDir, indexLock);
            return new(post.Id, DownloadStatus.Ok, post.Ext);
        }
        catch
        {
            return new(post.Id, DownloadStatus.Fail, post.Ext);
        }
    }

    // ── Главный метод — запуск всей загрузки ──────────────────────────────────

    public static async Task RunAsync(
        DownloadConfig cfg,
        Action<string> log,
        Action<double, string> setProgress,
        Action<string, string> setStatus,       // (text, colorHex)
        CancellationToken ct)
    {
        var folders = ScanFolders(cfg.BaseDir);
        if (folders.Count == 0)
        {
            log("! Нет папок в директории.");
            setStatus("нет папок", "#E74C3C");
            return;
        }

        log($"  Артистов: {folders.Count}");

        using var client = BuildClient();

        int totalOk = 0, totalSkip = 0, totalFail = 0;
        var startTime = DateTime.UtcNow;

        for (int fi = 0; fi < folders.Count; fi++)
        {
            if (ct.IsCancellationRequested) break;

            var folder = folders[fi];
            var tag    = Path.GetFileName(folder);

            log($"\n[ {tag} ]  — сбор постов...");
            setStatus($"сбор: {tag}", "#F39C12");

            var posts = await CollectAllPostsAsync(client, tag, cfg.ApiKey, cfg.UserId, ct);
            if (ct.IsCancellationRequested) break;

            if (posts.Count == 0)
            {
                log($"  {tag}: постов не найдено");
                setProgress((double)(fi + 1) / folders.Count, $"папка {fi + 1}/{folders.Count}");
                continue;
            }

            log($"  {tag}: найдено {posts.Count} постов");

            if (cfg.DryRun)
            {
                log($"  {tag}: dry-run, пропускаем");
                setProgress((double)(fi + 1) / folders.Count, $"папка {fi + 1}/{folders.Count}");
                continue;
            }

            var artistIndex = LoadIndex(folder);
            var artistLock  = new object();
            setStatus($"загрузка: {tag}", "#C0392B");

            int ok = 0, skip = 0, fail = 0;
            int done = 0;
            int total = posts.Count;

            // Параллельная загрузка с ограничением потоков
            var sem = new SemaphoreSlim(cfg.Threads);
            var tasks = posts.Select(async post =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var result = await DownloadFileAsync(
                        client, post, folder,
                        artistIndex, artistLock,
                        cfg.SkipExisting, ct);

                    lock (artistLock)
                    {
                        done++;
                        switch (result.Status)
                        {
                            case DownloadStatus.Ok:
                                ok++;
                                log($"  + {tag}/{GetSubfolder(result.Ext)}/{result.PostId}{result.Ext}");
                                break;
                            case DownloadStatus.Skip:
                                skip++;
                                break;
                            case DownloadStatus.Fail:
                                fail++;
                                log($"  ! {tag}/{result.PostId}{result.Ext}  FAIL");
                                break;
                        }

                        double inner   = (double)done / total;
                        double overall = (fi + inner) / folders.Count;
                        setProgress(overall, $"{tag}  {done}/{total}  ok={ok} skip={skip} fail={fail}");
                    }

                    return result;
                }
                finally { sem.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);

            totalOk   += ok;
            totalSkip += skip;
            totalFail += fail;
            log($"  {tag}: готово — ok={ok}  skip={skip}  fail={fail}");

            if (!ct.IsCancellationRequested)
                setProgress((double)(fi + 1) / folders.Count, $"папка {fi + 1}/{folders.Count}");
        }

        var elapsed = DateTime.UtcNow - startTime;
        var elapsedStr = elapsed.ToString(@"hh\:mm\:ss");

        if (ct.IsCancellationRequested)
        {
            log("\n── Остановлено пользователем ────────────────");
            setStatus("остановлено", "#F39C12");
        }
        else
        {
            log($"\n── Готово!  {elapsedStr}" +
                $"  скачано: {totalOk}" +
                $"  пропущено: {totalSkip}" +
                $"  ошибок: {totalFail}" +
                $" ────────");
            setStatus($"готово  ok={totalOk}  skip={totalSkip}  fail={totalFail}", "#27AE60");
            setProgress(1.0, $"Всего: {elapsedStr}");
        }
    }

    // ── Проверка доступа ──────────────────────────────────────────────────────

    public static async Task CheckAccessAsync(
        string apiKey, string userId,
        Action<string> log,
        CancellationToken ct)
    {
        using var client = BuildClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        log("── Проверка доступа ─────────────────────────");

        var checks = new[]
        {
            ("rule34.xxx (сайт)",    "https://rule34.xxx/"),
            ("api.rule34.xxx (API)", "https://api.rule34.xxx/index.php" +
                                     "?page=dapi&s=post&q=index&limit=1&tags=test"),
        };

        foreach (var (name, url) in checks)
        {
            try
            {
                var resp = await client.GetAsync(url, ct);
                var status = resp.StatusCode == HttpStatusCode.OK
                    ? $"OK ({(int)resp.StatusCode})"
                    : $"?? ({(int)resp.StatusCode})";
                log($"  {name} ... {status}");
            }
            catch (Exception ex)
            {
                log($"  {name} ... FAIL ({ex.Message})");
            }
        }

        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(userId))
        {
            log($"  API ключ (user_id={userId}) ...");
            try
            {
                var url  = $"{ApiUrl}?page=dapi&s=post&q=index&limit=1&tags=test" +
                           $"&api_key={Uri.EscapeDataString(apiKey)}" +
                           $"&user_id={Uri.EscapeDataString(userId)}";
                var resp = await client.GetAsync(url, ct);
                var msg  = resp.StatusCode switch
                {
                    HttpStatusCode.OK        => "OK (ключ принят)",
                    HttpStatusCode.Forbidden => "FAIL (403 — ключ отклонён)",
                    _                        => $"?? (статус {(int)resp.StatusCode})",
                };
                log($"  API ключ ... {msg}");
            }
            catch (Exception ex)
            {
                log($"  API ключ ... FAIL ({ex.Message})");
            }
        }
        else
        {
            log("  Авторизация не настроена — API_KEY и USER_ID пусты");
        }

        log("─────────────────────────────────────────────");
    }
}
