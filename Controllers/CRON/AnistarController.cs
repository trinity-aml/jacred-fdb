using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using JacRed.Engine.CORE;
using JacRed.Engine;
using JacRed.Models.tParse;
using JacRed.Models.Details;
using IO = System.IO;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using HttpClient = JacRed.Engine.CORE.HttpClient;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/anistar/[action]")]
    public class AnistarController : BaseController
    {
        // ===== ДИНАМИЧЕСКИЕ ЖАНРЫ =====
        // Подгружаются из бокового меню /anime/, кэшируются в файл.
        static readonly string GenresCachePath = "Data/temp/anistar_genres.json";
        static List<string> genreSlugs = LoadCachedGenres();

        static Dictionary<string, List<TaskParse>> taskParse = new Dictionary<string, List<TaskParse>>();

        static AnistarController()
        {
            if (IO.File.Exists("Data/temp/anistar_taskParse.json"))
                taskParse = JsonConvert.DeserializeObject<Dictionary<string, List<TaskParse>>>(IO.File.ReadAllText("Data/temp/anistar_taskParse.json"));
        }

        #region PUBLIC: Utilities

        /// <summary>
        /// Принудительно обновить список жанров из сайдбара и пересохранить кэш.
        /// </summary>
        [HttpGet]
        public async Task<string> RefreshGenres()
        {
            var ok = await EnsureGenresAsync(force: true);
            return ok ? $"ok ({genreSlugs.Count} genres)" : "fail";
        }

        #endregion

        #region Parse (ручной прогон одной страницы)
        static bool _workParse = false;

        [HttpGet]
        public async Task<string> Parse(int page = 1)
        {
            if (_workParse) return "work";
            _workParse = true;
            string log = "";

            try
            {
                await EnsureGenresAsync();

                // Пустой slug = общий каталог /anime/
                var slugs = new[] { "" }.Concat(genreSlugs).Distinct().ToList();

                foreach (string slug in slugs)
                {
                    await parsePage(slug, page);
                    log += $"{(string.IsNullOrEmpty(slug) ? "all" : slug)} - {page}\n";
                    await Task.Delay(AppInit.conf.Anistar.parseDelay);
                }
            }
            catch { }

            _workParse = false;
            return string.IsNullOrWhiteSpace(log) ? "ok" : log;
        }
        #endregion

        #region UpdateTasksParse (строим очередь задач)
        [HttpGet]
        public async Task<string> UpdateTasksParse()
        {
            await EnsureGenresAsync();

            // Пустой slug = общий каталог /anime/
            var slugs = new[] { "" }.Concat(genreSlugs).Distinct().ToList();

            foreach (string slug in slugs)
            {
                string url = catalogUrl(slug, 1);
                string html = await HttpClient.Get(url, timeoutSeconds: 15, useproxy: AppInit.conf.Anistar.useproxy);
                if (html == null) continue;

                int maxpages = 1;
                foreach (Match m in Regex.Matches(html, @"href\s*=\s*""[^""]*/page/([0-9]+)/""", RegexOptions.IgnoreCase))
                    if (int.TryParse(m.Groups[1].Value, out int p))
                        maxpages = Math.Max(maxpages, p);

                if (!taskParse.ContainsKey(slug))
                    taskParse.Add(slug, new List<TaskParse>());

                var list = taskParse[slug];

                for (int page = 1; page <= maxpages; page++)
                {
                    if (list.Find(i => i.page == page) == null)
                        list.Add(new TaskParse(page));
                }
            }

            IO.File.WriteAllText("Data/temp/anistar_taskParse.json", JsonConvert.SerializeObject(taskParse));
            return "ok";
        }
        #endregion

        #region ParseAllTask (выполняем очередь)
        static bool _parseAllTaskWork = false;

        [HttpGet]
        public async Task<string> ParseAllTask()
        {
            if (_parseAllTaskWork) return "work";
            _parseAllTaskWork = true;

            try
            {
                await EnsureGenresAsync();

                foreach (var kv in taskParse.ToArray())
                {
                    string slug = kv.Key;
                    foreach (var val in kv.Value.ToArray())
                    {
                        if (DateTime.Today == val.updateTime)
                            continue;

                        await Task.Delay(AppInit.conf.Anistar.parseDelay);
                        bool ok = await parsePage(slug, val.page);
                        if (ok)
                            val.updateTime = DateTime.Today;
                    }
                }
            }
            catch { }

            _parseAllTaskWork = false;
            return "ok";
        }
        #endregion

        #region parsePage
        async Task<bool> parsePage(string slug, int page)
        {
            string url = catalogUrl(slug, page);
            string html = await HttpClient.Get(url, timeoutSeconds: 20, useproxy: AppInit.conf.Anistar.useproxy);
            if (html == null) return false;

            var torrents = new List<TorrentBaseDetails>();

            var rows = Regex.Split(html, "<div class=\"news", RegexOptions.IgnoreCase).Skip(1).ToArray();

            foreach (string raw in rows)
            {
                string row = tParse.ReplaceBadNames(raw);
                if (string.IsNullOrWhiteSpace(row))
                    continue;

                // Ссылка на статью
                string article = rx(row, "<a[^>]+href=\"([^\"]+)\"[^>]*class=\"?title", 1);
                if (string.IsNullOrWhiteSpace(article))
                    article = rx(row, "<div class=\"title_left\">\\s*<a[^>]+href=\"([^\"]+)", 1);
                if (string.IsNullOrWhiteSpace(article)) continue;
                if (article.StartsWith("/"))
                    article = $"{AppInit.conf.Anistar.host}{article}";

                // Заголовок
                string title = HttpUtility.HtmlDecode(rx(row, "<a[^>]+class=\"?title[^\"]*\"[^>]*>([^<]+)</a>", 1));
                if (string.IsNullOrWhiteSpace(title))
                    title = HttpUtility.HtmlDecode(rx(row, "<div class=\"title_left\">\\s*<a[^>]*>([^<]+)</a>", 1));

                // Дата публикации в листинге
                DateTime createTime = parseRuDate(rx(row, "<i class=\"fa[^>]*fa-clock[^\"]*\"></i>([^<]+)<", 1));

                // Деталка
                var detail = await parseDetail(article);

                foreach (var tor in detail.torrents)
                {
                    string[] types = new[] { "anime" };

                    int sid = tor.seeds ?? 0;
                    int pir = tor.peers ?? 0;

                    string sizeName = humanSize(tor.sizeBytes);

                    string name = !string.IsNullOrWhiteSpace(detail.titleRu) ? detail.titleRu :
                                  (!string.IsNullOrWhiteSpace(detail.title) ? firstTitle(detail.title) : firstTitle(title));
                    string originalname = detail.titleEn;

                    torrents.Add(new TorrentBaseDetails
                    {
                        trackerName = "anistar",
                        types = types,
                        url = article,
                        title = title,
                        sid = sid,
                        pir = pir,
                        sizeName = sizeName,
                        magnet = tor.magnet,
                        createTime = tor.creationDateUtc != default ? tor.creationDateUtc : createTime,
                        name = name,
                        originalname = string.IsNullOrWhiteSpace(originalname) ? null : originalname,
                        relased = detail.year
                    });
                }

                await Task.Delay(AppInit.conf.Anistar.parseDelay);
            }

            FileDB.AddOrUpdate(torrents);
            return torrents.Count > 0;
        }
        #endregion

        #region parseDetail & DTO
        class DetailResult
        {
            public string title;
            public string titleRu;
            public string titleEn;
            public int year;
            public List<TorItem> torrents = new List<TorItem>();
        }

        class TorItem
        {
            public string magnet;
            public long sizeBytes;
            public DateTime creationDateUtc;
            public int? seeds;
            public int? peers;
        }

        async Task<DetailResult> parseDetail(string url)
        {
            var res = new DetailResult();
            string html = await HttpClient.Get(url, timeoutSeconds: 20, useproxy: AppInit.conf.Anistar.useproxy);
            if (html == null) return res;

            // Title / RU / EN
            res.title = HttpUtility.HtmlDecode(rx(html, "<h1[^>]*>([^<]+)</h1>", 1));
            splitTitles(res.title, out res.titleRu, out res.titleEn);

            // Год выпуска
            if (int.TryParse(Regex.Match(html, "Год выпуска[^0-9]+([12][0-9]{3})", RegexOptions.IgnoreCase).Groups[1].Value, out int y))
                res.year = y;

            // Ссылки на торренты (.torrent и gettorrent.php)
            var links = Regex.Matches(html, "href\\s*=\\s*\"([^\"]+(?:\\.torrent|engine/gettorrent\\.php\\?id=\\d+)[^\"]*)\"", RegexOptions.IgnoreCase)
                             .Cast<Match>()
                             .Select(m => m.Groups[1].Value)
                             .Distinct()
                             .ToList();

            foreach (var href in links)
            {
                string torrUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{AppInit.conf.Anistar.host}/{href.TrimStart('/')}";
                byte[] data = null;

                if (torrUrl.Contains("gettorrent.php", StringComparison.OrdinalIgnoreCase))
                    data = await FetchBytesAsync(torrUrl, referer: url, timeoutSeconds: 20, useproxy: AppInit.conf.Anistar.useproxy);
                else
                    data = await FetchBytesAsync(torrUrl, timeoutSeconds: 20, useproxy: AppInit.conf.Anistar.useproxy);

                if (data == null || data.Length == 0)
                    continue;

                if (!tryParseTorrentMinimal(data, out var infohashHex, out var creationUtc, out long totalSize, out var trackers))
                    continue;

                string magnet = buildMagnet(infohashHex, name: res.title, trackers);

                int? seeds = null, peers = null;
                try { (seeds, peers) = await scrapeHttpTrackers(infohashHex, trackers); } catch { }

                res.torrents.Add(new TorItem
                {
                    magnet = magnet,
                    sizeBytes = totalSize,
                    creationDateUtc = creationUtc,
                    seeds = seeds,
                    peers = peers
                });

                await Task.Delay(200);
            }

            return res;
        }
        #endregion

        #region DYNAMIC GENRES (sidebar)
        static List<string> LoadCachedGenres()
        {
            try
            {
                if (IO.File.Exists(GenresCachePath))
                {
                    var list = JsonConvert.DeserializeObject<List<string>>(IO.File.ReadAllText(GenresCachePath));
                    if (list != null) return list;
                }
            }
            catch { }
            return new List<string>();
        }

        async Task<bool> EnsureGenresAsync(bool force = false)
        {
            if (!force && genreSlugs.Count > 0) return true;

            try
            {
                string url = $"{AppInit.conf.Anistar.rqHost().TrimEnd('/')}/anime/";
                string html = await HttpClient.Get(url, timeoutSeconds: 15, useproxy: AppInit.conf.Anistar.useproxy);
                if (html == null) return genreSlugs.Count > 0;

                // Парсим ссылки вида /anime/<slug>/ ИЛИ абсолютные https://.../anime/<slug>/
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Match m in Regex.Matches(html, @"href\s*=\s*""(?<u>(?:https?://[^""]+)?/anime/(?<slug>[a-z0-9\-]+)/)""", RegexOptions.IgnoreCase))
                {
                    var slug = m.Groups["slug"].Value.Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(slug)) continue;
                    if (slug is "page" or "feed" or "rss") continue;
                    found.Add(slug);
                }

                genreSlugs = found.OrderBy(s => s).ToList();

                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(GenresCachePath));
                IO.File.WriteAllText(GenresCachePath, JsonConvert.SerializeObject(genreSlugs));

                return genreSlugs.Count > 0;
            }
            catch
            {
                return genreSlugs.Count > 0; // оставим старый кэш, если есть
            }
        }
        #endregion

        #region helpers: html & text
        static string rx(string input, string pattern, int index = 1)
        {
            var m = Regex.Match(input ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return HttpUtility.HtmlDecode(m.Success ? m.Groups[index].Value.Trim() : "");
        }

        static DateTime parseRuDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return DateTime.UtcNow;

            s = Regex.Replace(s, "[\n\r\t ]+", " ").Trim();

            var now = DateTime.UtcNow;
            if (s.StartsWith("Сегодня", StringComparison.OrdinalIgnoreCase))
                return now;
            if (s.StartsWith("Вчера", StringComparison.OrdinalIgnoreCase))
                return now.AddDays(-1);

            var ru = new CultureInfo("ru-RU");
            var fmts = new[]
            {
                "d MMMM yyyy, HH:mm", "dd MMMM yyyy, HH:mm",
                "d.MM.yyyy, HH:mm",   "dd.MM.yyyy, HH:mm",
                "d MMMM, HH:mm",      "dd MMMM, HH:mm",
                "dd.MM.yyyy", "d.MM.yyyy"
            };

            foreach (var f in fmts)
                if (DateTime.TryParseExact(s, f, ru, DateTimeStyles.AssumeUniversal, out var dt))
                    return dt.ToUniversalTime();

            return now;
        }

        static void splitTitles(string raw, out string titleRu, out string titleEn)
        {
            titleRu = null; titleEn = null;
            if (string.IsNullOrWhiteSpace(raw)) return;

            var parts = Regex.Split(raw, "\\s*(/|\\||,|•| - )\\s*", RegexOptions.IgnoreCase)
                             .Where(p => !Regex.IsMatch(p, "^(/|\\||,|•| - )$", RegexOptions.IgnoreCase))
                             .Select(p => p.Trim())
                             .ToArray();

            titleRu = parts.FirstOrDefault(p => Regex.IsMatch(p, @"\p{IsCyrillic}"));
            titleEn = parts.LastOrDefault(p => Regex.IsMatch(p, @"[A-Za-z]"));

            if (string.IsNullOrWhiteSpace(titleRu))
                titleRu = raw;
        }

        static string firstTitle(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return t;
            return Regex.Split(t, "(\\[|\\/|\\(|\\|)", RegexOptions.IgnoreCase)[0].Trim();
        }

        static string humanSize(long bytes)
        {
            if (bytes <= 0) return null;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double v = bytes;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.##} {u[i]}";
        }

        static string catalogUrl(string slug, int page)
        {
            string baseCat = string.IsNullOrWhiteSpace(slug) ? "anime" : $"anime/{slug}";
            string path = page <= 1 ? $"{baseCat}/" : $"{baseCat}/page/{page}/";
            return $"{AppInit.conf.Anistar.rqHost().TrimEnd('/')}/{path}";
        }
        #endregion

        #region helpers: torrent minimal parser (bencode)
        // Мини-парсер bencode: infohash(btih), creation date, total size, trackers
        static bool tryParseTorrentMinimal(byte[] data, out string infohashHex, out DateTime creationUtc, out long totalSize, out List<string> trackers)
        {
            infohashHex = null;
            creationUtc = DateTime.MinValue;
            totalSize = 0;
            trackers = new List<string>();

            try
            {
                int idx = 0;
                if (data[idx] != (byte)'d') return false; // корневой словарь
                idx++;

                byte[] infoSpan = null;

                while (idx < data.Length && data[idx] != (byte)'e')
                {
                    string key = readString(data, ref idx);
                    if (key == null) break;

                    if (key == "info")
                    {
                        int startInfo = idx;
                        int endInfo = skipElement(data, ref idx);
                        if (endInfo <= startInfo) return false;
                        infoSpan = data.Skip(startInfo).Take(endInfo - startInfo).ToArray();
                    }
                    else if (key == "creation date")
                    {
                        long ts = readNumber(data, ref idx);
                        if (ts > 0)
                            creationUtc = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
                    }
                    else if (key == "announce")
                    {
                        string a = readString(data, ref idx);
                        if (!string.IsNullOrWhiteSpace(a))
                            trackers.Add(a);
                    }
                    else if (key == "announce-list")
                    {
                        if (data[idx] != (byte)'l') { skipElement(data, ref idx); }
                        else
                        {
                            idx++;
                            while (idx < data.Length && data[idx] != (byte)'e')
                            {
                                if (data[idx] == (byte)'l')
                                {
                                    idx++;
                                    while (idx < data.Length && data[idx] != (byte)'e')
                                    {
                                        string t = readString(data, ref idx);
                                        if (!string.IsNullOrWhiteSpace(t))
                                            trackers.Add(t);
                                    }
                                    if (idx < data.Length && data[idx] == (byte)'e') idx++;
                                }
                                else { skipElement(data, ref idx); }
                            }
                            if (idx < data.Length && data[idx] == (byte)'e') idx++;
                        }
                    }
                    else if (key == "length")
                    {
                        long ln = readNumber(data, ref idx);
                        if (ln > 0) totalSize += ln;
                    }
                    else if (key == "files")
                    {
                        if (data[idx] != (byte)'l') { skipElement(data, ref idx); }
                        else
                        {
                            idx++;
                            while (idx < data.Length && data[idx] != (byte)'e')
                            {
                                if (data[idx] != (byte)'d') { skipElement(data, ref idx); continue; }
                                idx++;
                                while (idx < data.Length && data[idx] != (byte)'e')
                                {
                                    string fkey = readString(data, ref idx);
                                    if (fkey == null) { idx = data.Length; break; }

                                    if (fkey == "length")
                                    {
                                        long ln = readNumber(data, ref idx);
                                        if (ln > 0) totalSize += ln;
                                    }
                                    else { skipElement(data, ref idx); }
                                }
                                if (idx < data.Length && data[idx] == (byte)'e') idx++;
                            }
                            if (idx < data.Length && data[idx] == (byte)'e') idx++;
                        }
                    }
                    else
                    {
                        skipElement(data, ref idx);
                    }
                }

                if (idx < data.Length && data[idx] == (byte)'e') idx++;

                if (infoSpan == null) return false;

                using (var sha1 = SHA1.Create())
                {
                    var hash = sha1.ComputeHash(infoSpan);
                    infohashHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }

                trackers = trackers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return !string.IsNullOrWhiteSpace(infohashHex);
            }
            catch { return false; }
        }

        static string readString(byte[] data, ref int idx)
        {
            int start = idx;
            int colon = Array.IndexOf<byte>(data, (byte)':', start);
            if (colon < 0) return null;
            var lenStr = Encoding.ASCII.GetString(data, start, colon - start);
            if (!int.TryParse(lenStr, out int len)) return null;
            idx = colon + 1;
            if (idx + len > data.Length) return null;
            var s = Encoding.UTF8.GetString(data, idx, len);
            idx += len;
            return s;
        }

        static long readNumber(byte[] data, ref int idx)
        {
            if (data[idx] != (byte)'i') { skipElement(data, ref idx); return 0; }
            idx++;
            int start = idx;
            int end = Array.IndexOf<byte>(data, (byte)'e', start);
            if (end < 0) { idx = data.Length; return 0; }
            var num = Encoding.ASCII.GetString(data, start, end - start);
            idx = end + 1;
            if (long.TryParse(num, out long v)) return v;
            return 0;
        }

        static int skipElement(byte[] data, ref int idx)
        {
            int start = idx;
            if (idx >= data.Length) return idx;

            switch (data[idx])
            {
                case (byte)'i':
                    idx++;
                    while (idx < data.Length && data[idx] != (byte)'e') idx++;
                    if (idx < data.Length) idx++;
                    break;

                case (byte)'l':
                    idx++;
                    while (idx < data.Length && data[idx] != (byte)'e')
                        skipElement(data, ref idx);
                    if (idx < data.Length) idx++;
                    break;

                case (byte)'d':
                    idx++;
                    while (idx < data.Length && data[idx] != (byte)'e')
                    {
                        var _ = readString(data, ref idx);
                        skipElement(data, ref idx);
                    }
                    if (idx < data.Length) idx++;
                    break;

                default:
                    // string
                    var _s = readString(data, ref idx);
                    break;
            }
            return idx;
        }

        static string buildMagnet(string infohashHex, string name, List<string> trackers)
        {
            var sb = new StringBuilder("magnet:?xt=urn:btih:");
            sb.Append(infohashHex);
            if (!string.IsNullOrWhiteSpace(name))
                sb.Append("&dn=").Append(Uri.EscapeDataString(name));
            foreach (var tr in trackers.Take(30))
                sb.Append("&tr=").Append(Uri.EscapeDataString(tr));
            return sb.ToString();
        }
        #endregion

        #region helpers: HTTP-scrape (best-effort)
        // Простой HTTP/HTTPS scrape: /announce -> /scrape?info_hash=%..    
        async Task<(int? seeds, int? peers)> scrapeHttpTrackers(string infohashHex, List<string> trackers)
        {
            try
            {
                var ih = hexToBytes(infohashHex);
                foreach (var tr in trackers.Distinct().Where(t => t.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
                {
                    string scrape = toScrapeUrl(tr);
                    if (scrape == null) continue;

                    string url = $"{scrape}?info_hash={percentEncode(ih)}";
                    byte[] resp = await FetchBytesAsync(url, timeoutSeconds: 7, useproxy: AppInit.conf.Anistar.useproxy);
                    if (resp == null || resp.Length == 0) continue;

                    if (tryParseScrape(resp, ih, out int? seeds, out int? peers))
                        return (seeds, peers);
                }
            }
            catch { }
            return (null, null);
        }

        static bool tryParseScrape(byte[] data, byte[] infohash, out int? seeds, out int? peers)
        {
            seeds = null; peers = null;
            int idx = 0;
            if (idx >= data.Length || data[idx] != (byte)'d') return false;
            idx++;
            while (idx < data.Length && data[idx] != (byte)'e')
            {
                string key = readString(data, ref idx);
                if (key == "files")
                {
                    if (data[idx] != (byte)'d') { skipElement(data, ref idx); break; }
                    idx++;
                    while (idx < data.Length && data[idx] != (byte)'e')
                    {
                        string k = readStringRaw(data, ref idx, out byte[] kraw);
                        if (k == null) { skipElement(data, ref idx); continue; }

                        if (data[idx] == (byte)'d')
                        {
                            idx++;
                            int? complete = null, incomplete = null;
                            while (idx < data.Length && data[idx] != (byte)'e')
                            {
                                string kk = readString(data, ref idx);
                                if (kk == "complete") complete = (int)readNumber(data, ref idx);
                                else if (kk == "incomplete") incomplete = (int)readNumber(data, ref idx);
                                else skipElement(data, ref idx);
                            }
                            if (idx < data.Length && data[idx] == (byte)'e') idx++;

                            if (bytesEq(kraw, infohash))
                            {
                                seeds = complete; peers = incomplete;
                                return true;
                            }
                        }
                        else skipElement(data, ref idx);
                    }
                    if (idx < data.Length && data[idx] == (byte)'e') idx++;
                }
                else skipElement(data, ref idx);
            }
            return false;
        }

        static string readStringRaw(byte[] data, ref int idx, out byte[] raw)
        {
            raw = null;
            int start = idx;
            int colon = Array.IndexOf<byte>(data, (byte)':', start);
            if (colon < 0) return null;
            var lenStr = Encoding.ASCII.GetString(data, start, colon - start);
            if (!int.TryParse(lenStr, out int len)) return null;
            idx = colon + 1;
            if (idx + len > data.Length) return null;
            raw = new byte[len];
            Buffer.BlockCopy(data, idx, raw, 0, len);
            idx += len;
            return "(raw)";
        }

        static string toScrapeUrl(string announce)
        {
            try
            {
                var uri = new Uri(announce);
                string path = uri.AbsolutePath;
                if (path.IndexOf("announce", StringComparison.OrdinalIgnoreCase) >= 0)
                    path = Regex.Replace(path, "announce", "scrape", RegexOptions.IgnoreCase);
                else if (path.IndexOf("scrape", StringComparison.OrdinalIgnoreCase) < 0)
                    return null;
                var ub = new UriBuilder(uri) { Path = path, Query = "" };
                return ub.Uri.ToString();
            }
            catch { return null; }
        }

        static string percentEncode(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 3);
            foreach (var b in bytes) sb.Append('%').Append(b.ToString("X2"));
            return sb.ToString();
        }

        static byte[] hexToBytes(string hex)
        {
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            var data = new byte[hex.Length / 2];
            for (int i = 0; i < data.Length; i++)
                data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return data;
        }

        static bool bytesEq(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        #endregion

        #region BYTES FETCH (замена HttpClient.GetBytes)
        // Скачивание бинарных данных (торрент-файл или scrape-ответ), с поддержкой Referer и таймаута.
        // Не конфликтует с JacRed.Engine.CORE.HttpClient, т.к. используем System.Net.Http.HttpClient внутри.
        async Task<byte[]> FetchBytesAsync(string url, string referer = null, int timeoutSeconds = 20, bool useproxy = false)
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                // Если в проекте есть централизованная прокси — подключи её здесь:
                // Proxy = ..., UseProxy = useproxy
            };

            using var http = new System.Net.Http.HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Anistar.JacRed/1.1)");

            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(referer))
                req.Headers.Referrer = new Uri(referer);

            req.Headers.Accept.ParseAdd("application/x-bittorrent, application/octet-stream, text/plain, */*");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync();
        }
        #endregion
    }
}
