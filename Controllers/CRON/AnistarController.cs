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
using System.Net.Http.Headers;
using HttpClient = JacRed.Engine.CORE.HttpClient;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/anistar/[action]")]
    public class AnistarController : BaseController
    {
        // ===== ДИНАМИЧЕСКИЕ ЖАНРЫ =====
        // Абсолютный путь, чтобы не зависеть от WorkingDirectory
        static readonly string GenresCachePath = System.IO.Path.Combine(
            AppContext.BaseDirectory, "Data", "temp", "anistar_genres.json");

        static List<string> genreSlugs = LoadCachedGenres();

        static Dictionary<string, List<TaskParse>> taskParse = new Dictionary<string, List<TaskParse>>();

        static AnistarController()
        {
            if (IO.File.Exists("Data/temp/anistar_taskParse.json"))
                taskParse = JsonConvert.DeserializeObject<Dictionary<string, List<TaskParse>>>(IO.File.ReadAllText("Data/temp/anistar_taskParse.json"));
        }

        #region PUBLIC: Utilities
        [HttpGet]
        public async Task<string> RefreshGenres()
        {
            var ok = await EnsureGenresAsync(force: true);
            var exists = IO.File.Exists(GenresCachePath);
            var size = exists ? new IO.FileInfo(GenresCachePath).Length : 0;

            return $"{(ok ? "ok" : "fail")} (genres: {genreSlugs.Count}, file: {(exists ? "exists" : "absent")}, size: {size}b, path: {GenresCachePath})";
        }
        #endregion

        #region Parse (page<=0 => все страницы)
        static bool _workParse = false;

        /// <summary>
        /// Если page <= 0 — парсит все страницы каждого жанра, иначе только указанную страницу.
        /// </summary>
        [HttpGet]
        public async Task<string> Parse(int page = 0)
        {
            if (_workParse) return "work";
            _workParse = true;
            var sb = new StringBuilder();

            try
            {
                await EnsureGenresAsync();

                var slugs = new[] { "" }.Concat(genreSlugs).Distinct().ToList();

                foreach (string slug in slugs)
                {
                    int maxpages = page > 0 ? page : await GetMaxPagesForSlugAsync(slug);
                    int start = page > 0 ? page : 1;

                    for (int p = start; p <= maxpages; p++)
                    {
                        await parsePage(slug, p);
                        sb.AppendLine($"{(string.IsNullOrEmpty(slug) ? "all" : slug)} - page {p}");
                        await Task.Delay(AppInit.conf.Anistar.parseDelay);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("error: " + ex.Message);
            }

            _workParse = false;
            var log = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(log) ? "ok" : log;
        }
        #endregion

        #region UpdateTasksParse
        [HttpGet]
        public async Task<string> UpdateTasksParse()
        {
            await EnsureGenresAsync();

            var slugs = new[] { "" }.Concat(genreSlugs).Distinct().ToList();

            foreach (string slug in slugs)
            {
                int maxpages = await GetMaxPagesForSlugAsync(slug);

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

        #region ParseAllTask
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

        #region parsePage + helpers
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

                // Заголовок (листинг)
                string listTitle = HttpUtility.HtmlDecode(rx(row, "<a[^>]+class=\"?title[^\"]*\"[^>]*>([^<]+)</a>", 1));
                if (string.IsNullOrWhiteSpace(listTitle))
                    listTitle = HttpUtility.HtmlDecode(rx(row, "<div class=\"title_left\">\\s*<a[^>]*>([^<]+)</a>", 1));

                // Дата публикации в листинге
                DateTime createTime = parseRuDate(rx(row, "<i class=\"fa[^>]*fa-clock[^\"]*\"></i>([^<]+)<", 1));

                // Деталка (с анти-бот ретраями)
                var detail = await parseDetail(article);

                foreach (var tor in detail.torrents)
                {
                    string[] types = new[] { "anime" };
                    int sid = tor.seeds ?? 0;
                    int pir = tor.peers ?? 0;
                    string sizeName = humanSize(tor.sizeBytes);

                    // Итоговый заголовок "<RU> / <EN> [Ep..][Q]"
                    string finalTitle = BuildTitleWithTags(detail.titleRu, detail.titleEn, tor.episodeTag, tor.qualityTag);

                    // URL c index=<номер эпизода>
                    int epIndex = tor.episodeIndex ?? 1;
                    string urlWithIndex = AppendIndexQuery(article, epIndex);

                    torrents.Add(new TorrentBaseDetails
                    {
                        trackerName = "anistar",
                        types = types,
                        url = urlWithIndex,
                        title = finalTitle,
                        sid = sid,
                        pir = pir,
                        sizeName = sizeName,
                        magnet = tor.magnet,
                        createTime = tor.creationDateUtc != default ? tor.creationDateUtc : createTime,
                        name = detail.titleRu ?? firstTitle(listTitle),
                        originalname = string.IsNullOrWhiteSpace(detail.titleEn) ? null : detail.titleEn,
                        relased = detail.year
                    });
                }

                await Task.Delay(AppInit.conf.Anistar.parseDelay);
            }

            FileDB.AddOrUpdate(torrents);
            return torrents.Count > 0;
        }

        /// <summary>
        /// Определить число страниц пагинации для жанра/каталога.
        /// </summary>
        async Task<int> GetMaxPagesForSlugAsync(string slug)
        {
            try
            {
                string url = catalogUrl(slug, 1);
                string html = await HttpClient.Get(url, timeoutSeconds: 15, useproxy: AppInit.conf.Anistar.useproxy);
                if (html == null) return 1;

                int maxpages = 1;
                foreach (Match m in Regex.Matches(html, @"href\s*=\s*""[^""]*/page/([0-9]+)/""", RegexOptions.IgnoreCase))
                    if (int.TryParse(m.Groups[1].Value, out int p))
                        maxpages = Math.Max(maxpages, p);

                return Math.Max(1, maxpages);
            }
            catch
            {
                return 1;
            }
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

            // эпизодность/качество/дедуп
            public string episodeTag;   // "Ep 03" / "S01E07"
            public string qualityTag;   // "1080p" / "WEB-DL"
            public string infohashHex;  // для дедупа
            public int? episodeIndex;   // числовой индекс для url?index=
        }

        async Task<DetailResult> parseDetail(string url)
        {
            var res = new DetailResult();

            // === HTML деталки с анти-бот ретраями ===
            string html = await GetPageHtmlWithRetries(url, referer: null, useproxy: AppInit.conf.Anistar.useproxy);
            if (html == null) return res;

            // Title / RU / EN
            res.title = HttpUtility.HtmlDecode(rx(html, "<h1[^>]*>([^<]+)</h1>", 1));
            splitTitles(res.title, out res.titleRu, out res.titleEn);

            // Год выпуска
            if (int.TryParse(Regex.Match(html, "Год выпуска[^0-9]+([12][0-9]{3})", RegexOptions.IgnoreCase).Groups[1].Value, out int y))
                res.year = y;

            // Ссылки на торренты (.torrent и gettorrent.php) + контекст + info_d1
            var linkMatches = Regex.Matches(
                html,
                "<a[^>]+href\\s*=\\s*\"([^\"]+(?:\\.torrent|engine/gettorrent\\.php\\?id=\\d+)[^\"]*)\"[^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var links = new List<(string href, string label, string near, string infod1)>();

            foreach (Match m in linkMatches)
            {
                string href = m.Groups[1].Value;
                string inner = m.Groups[2].Value;

                string label = HttpUtility.HtmlDecode(Regex.Replace(inner, "<.*?>", "").Trim());

                // Текст внутри <div class="info_d1">…</div> (например: "Серия 5 (337.88 Mb)")
                string infod1 = null;
                var infod1m = Regex.Match(inner, "<div\\s+class\\s*=\\s*\"info_d1\"[^>]*>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (infod1m.Success)
                    infod1 = HttpUtility.HtmlDecode(Regex.Replace(infod1m.Groups[1].Value, "<.*?>", "").Trim());

                // Окружение на случай пустого label
                string near = "";
                if (string.IsNullOrWhiteSpace(label))
                {
                    int start = Math.Max(0, m.Index - 120);
                    int len = Math.Min(260, html.Length - start);
                    near = HttpUtility.HtmlDecode(Regex.Replace(html.Substring(start, len), "<.*?>", ""));
                }

                links.Add((href, label, near, infod1));
            }

            if (links.Count == 0)
            {
                foreach (Match m in Regex.Matches(html, "href\\s*=\\s*\"([^\"]+(?:\\.torrent|engine/gettorrent\\.php\\?id=\\d+)[^\"]*)\"", RegexOptions.IgnoreCase))
                    links.Add((m.Groups[1].Value, "", "", null));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int sequentialCounter = 1;

            foreach (var link in links)
            {
                string torrUrl = link.href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? link.href
                    : $"{AppInit.conf.Anistar.host}/{link.href.TrimStart('/')}";

                // Тянем торрент: байты + имя файла (с ретраями и анти-бот логикой)
                byte[] data;
                string fileName;
                (data, fileName) = await FetchTorrentWithRetries(torrUrl, referer: url, useproxy: AppInit.conf.Anistar.useproxy);

                if (data == null || data.Length == 0)
                    continue;

                if (!tryParseTorrentMinimal(data, out var infohashHex, out var creationUtc, out long totalSize, out var trackers))
                    continue;

                // Дедуп по infohash
                if (!string.IsNullOrWhiteSpace(infohashHex) && !seen.Add(infohashHex))
                    continue;

                // --- Используем info_d1 (эпизод и размер со страницы) ---
                var (epFromInfoD1, epTagFromInfoD1, sizeFromInfoD1) = ParseInfoD1(link.infod1);

                // Эпизод/качество из подписи/окружения (как было)
                string context = string.Join(" ", new[] { link.label, link.near }.Where(s => !string.IsNullOrWhiteSpace(s)));
                string epTag = DetectEpisodeTag(context);
                string qaTag = DetectQualityTag(context);

                // Приоритет эпизода: info_d1 → epTag → имя .torrent → порядковый
                int? epIndex = epFromInfoD1 ?? TryParseEpisodeNumber(epTag);
                if (epIndex == null || epIndex <= 0)
                {
                    var inferred = TryParseEpisodeNumberFromFileName(fileName);
                    if (inferred != null && inferred > 0)
                        epIndex = inferred;
                }
                if (epIndex == null || epIndex <= 0)
                    epIndex = sequentialCounter;

                sequentialCounter++;

                // Итоговая метка эпизода: если в info_d1 есть — используем её
                if (!string.IsNullOrWhiteSpace(epTagFromInfoD1))
                    epTag = epTagFromInfoD1;

                // Размер: если на кнопке указан и он >0 — используем его
                if (sizeFromInfoD1 > 0)
                    totalSize = sizeFromInfoD1;

                string magnet = buildMagnet(infohashHex, name: res.title, trackers);

                int? seeds = null, peers = null;
                try { (seeds, peers) = await scrapeHttpTrackers(infohashHex, trackers); } catch { }

                res.torrents.Add(new TorItem
                {
                    magnet = magnet,
                    sizeBytes = totalSize,
                    creationDateUtc = creationUtc,
                    seeds = seeds,
                    peers = peers,
                    infohashHex = infohashHex,
                    episodeTag = epTag,
                    qualityTag = qaTag,
                    episodeIndex = epIndex
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

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string host = null;

            try
            {
                host = AppInit.conf?.Anistar?.rqHost();
                if (string.IsNullOrWhiteSpace(host))
                    host = AppInit.conf?.Anistar?.host;

                if (string.IsNullOrWhiteSpace(host))
                    throw new Exception("Anistar host is not configured (rqHost/host is null or empty).");

                host = host.TrimEnd('/');
                string url = $"{host}/anime/";

                string html = await HttpClient.Get(url, timeoutSeconds: 20, useproxy: AppInit.conf.Anistar.useproxy);
                if (string.IsNullOrWhiteSpace(html))
                    throw new Exception("Empty HTML for /anime/ (anti-bot or network).");

                // Основной паттерн: /anime/<slug>/
                foreach (Match m in Regex.Matches(html, @"href\s*=\s*""(?<u>(?:https?://[^""]+)?/anime/(?<slug>[a-z0-9\-]+)/)""",
                                                  RegexOptions.IgnoreCase))
                {
                    var slug = m.Groups["slug"].Value.Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(slug) && slug is not ("page" or "feed" or "rss"))
                        found.Add(slug);
                }

                // Fallback (без завершающего "/")
                if (found.Count == 0)
                {
                    foreach (Match m in Regex.Matches(html, @"href\s*=\s*""(?<u>(?:https?://[^""]+)?/anime/(?<slug>[a-z0-9\-]+))""",
                                                      RegexOptions.IgnoreCase))
                    {
                        var slug = m.Groups["slug"].Value.Trim().ToLowerInvariant();
                        if (!string.IsNullOrWhiteSpace(slug) && !slug.Contains("page"))
                            found.Add(slug);
                    }
                }

                // Fallback по блоку жанров
                if (found.Count == 0)
                {
                    var block = Regex.Match(html, @"Жанры.*?</ul>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (block.Success)
                    {
                        foreach (Match m in Regex.Matches(block.Value, @"/anime/(?<slug>[a-z0-9\-]+)/?", RegexOptions.IgnoreCase))
                        {
                            var slug = m.Groups["slug"].Value.Trim().ToLowerInvariant();
                            if (!string.IsNullOrWhiteSpace(slug))
                                found.Add(slug);
                        }
                    }
                }
            }
            catch
            {
                // проглатываем — запись кэша произойдёт ниже
            }
            finally
            {
                try
                {
                    // Обновляем память и ВСЕГДА пишем файл (даже пустой список)
                    if (found.Count > 0)
                        genreSlugs = found.OrderBy(s => s).ToList();

                    var dir = System.IO.Path.GetDirectoryName(GenresCachePath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        IO.Directory.CreateDirectory(dir);

                    IO.File.WriteAllText(GenresCachePath, JsonConvert.SerializeObject(genreSlugs ?? new List<string>()));
                }
                catch { /* можно залогировать */ }
            }

            return genreSlugs.Count > 0;
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

        static string BuildTitleWithTags(string titleRu, string titleEn, string epTag, string qaTag)
        {
            var baseTitle = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(titleRu))
                baseTitle.Append(titleRu.Trim());
            if (!string.IsNullOrWhiteSpace(titleEn))
            {
                if (baseTitle.Length > 0) baseTitle.Append(" / ");
                baseTitle.Append(titleEn.Trim());
            }

            var tags = new List<string>();
            if (!string.IsNullOrWhiteSpace(epTag)) tags.Add(epTag.Trim());
            if (!string.IsNullOrWhiteSpace(qaTag)) tags.Add(qaTag.Trim());

            if (tags.Count > 0)
                baseTitle.Append(' ').Append('[').Append(string.Join("][", tags)).Append(']');

            return baseTitle.ToString();
        }

        static string AppendIndexQuery(string baseUrl, int episodeIndex)
        {
            if (episodeIndex <= 0) episodeIndex = 1;
            var sep = baseUrl.Contains("?") ? "&" : "?";
            return $"{baseUrl}{sep}index={episodeIndex}";
        }
        #endregion

        #region helpers: эпизод/качество/индекс/InfoD1
        static string DetectEpisodeTag(string context)
        {
            if (string.IsNullOrWhiteSpace(context)) return null;
            context = Regex.Replace(HttpUtility.HtmlDecode(context), @"\s+", " ").Trim();

            // "1 серия", "03 серия", "серия 12", "ep 3", "episode 03"
            var m1 = Regex.Match(context, @"\b([0-9]{1,3})\s*(?:сер(ия)?|ep(isode)?)\b", RegexOptions.IgnoreCase);
            if (m1.Success) return $"Ep {int.Parse(m1.Groups[1].Value):00}";

            // S01E03
            var m2 = Regex.Match(context, @"\bs[ _\-\.]?(?<s>\d{1,2})e[ _\-\.]?(?<e>\d{1,3})\b", RegexOptions.IgnoreCase);
            if (m2.Success) return $"S{int.Parse(m2.Groups["s"].Value):00}E{int.Parse(m2.Groups["e"].Value):00}";

            // Просто номер (fallback)
            var m3 = Regex.Match(context, @"\b([0-9]{1,3})\b");
            if (m3.Success) return $"Ep {int.Parse(m3.Groups[1].Value):00}";

            return null;
        }

        static string DetectQualityTag(string context)
        {
            if (string.IsNullOrWhiteSpace(context)) return null;
            context = Regex.Replace(HttpUtility.HtmlDecode(context), @"\s+", " ").Trim();

            var q1 = Regex.Match(context, @"\b(2160|1080|720|480)p\b", RegexOptions.IgnoreCase);
            if (q1.Success) return q1.Value.ToUpperInvariant();

            var q2 = Regex.Match(context, @"\b(WEB[- ]?DL|WEB[- ]?Rip|BDRip|BRRip|HDRip|HDTV|DVDRip|BluRay)\b", RegexOptions.IgnoreCase);
            if (q2.Success) return q2.Value.ToUpperInvariant();

            return null;
        }

        static int? TryParseEpisodeNumber(string epTag)
        {
            if (string.IsNullOrWhiteSpace(epTag)) return null;

            // "Ep 03"
            var m1 = Regex.Match(epTag, @"Ep\s*(\d{1,3})", RegexOptions.IgnoreCase);
            if (m1.Success && int.TryParse(m1.Groups[1].Value, out int n1)) return n1;

            // "S01E07"
            var m2 = Regex.Match(epTag, @"E(\d{1,3})", RegexOptions.IgnoreCase);
            if (m2.Success && int.TryParse(m2.Groups[1].Value, out int n2)) return n2;

            // Просто число
            if (int.TryParse(epTag, out int n)) return n;

            return null;
        }

        static int? TryParseEpisodeNumberFromFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            var name = IO.Path.GetFileNameWithoutExtension(fileName)?.Replace('_', ' ') ?? fileName;

            // S01E07 / s1e7 / S1.E07
            var m1 = Regex.Match(name, @"\bS(\d{1,2})[^0-9]?E(\d{1,3})\b", RegexOptions.IgnoreCase);
            if (m1.Success && int.TryParse(m1.Groups[2].Value, out int e1)) return e1;

            // Ep 03 / E03 / ep03
            var m2 = Regex.Match(name, @"\b(?:EP|E)[^0-9]?(\d{1,3})\b", RegexOptions.IgnoreCase);
            if (m2.Success && int.TryParse(m2.Groups[1].Value, out int e2)) return e2;

            // "12 серия" / "серия 12"
            var m3 = Regex.Match(name, @"\b([0-9]{1,3})\s*сер(ия)?\b", RegexOptions.IgnoreCase);
            if (m3.Success && int.TryParse(m3.Groups[1].Value, out int e3)) return e3;

            // Просто число
            var m4 = Regex.Match(name, @"\b([0-9]{1,3})\b");
            if (m4.Success && int.TryParse(m4.Groups[1].Value, out int e4)) return e4;

            return null;
        }

        // Парсинг текста вида: "Серия 5 (337.88 Mb)"
        static (int? epIndex, string epTag, long sizeBytes) ParseInfoD1(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return (null, null, 0);

            // Примеры: "Серия 5 (337.88 Mb)" / "серия 12 (1.4 GB)"
            var m = Regex.Match(text, @"серия\s*(?<ep>\d+)\s*\((?<sz>[\d\.,]+)\s*(?<unit>[KMGTP]?B|KB|MB|GB|TB|Kb|Mb|Gb|Tb|КБ|МБ|ГБ|ТБ)\)", RegexOptions.IgnoreCase);
            int? ep = null; long bytes = 0; string tag = null;

            if (m.Success)
            {
                if (int.TryParse(m.Groups["ep"].Value, out int e)) ep = e;

                var szStr = m.Groups["sz"].Value.Replace(',', '.');
                double.TryParse(szStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double sz);

                var unit = m.Groups["unit"].Value.ToUpperInvariant();
                double mul = unit switch
                {
                    "KB" or "KБ" or "КБ" => 1024d,
                    "MB" or "MБ" or "МБ" => 1024d * 1024,
                    "GB" or "GБ" or "ГБ" => 1024d * 1024 * 1024,
                    "TB" or "TБ" or "ТБ" => 1024d * 1024 * 1024 * 1024,
                    _ => 1d
                };
                bytes = (long)Math.Round(sz * mul);

                tag = $"Ep {e:00}";
            }
            else
            {
                // fallback: просто число => эпизод
                var m2 = Regex.Match(text, @"\b(\d{1,3})\b");
                if (m2.Success && int.TryParse(m2.Groups[1].Value, out int e2))
                {
                    ep = e2;
                    tag = $"Ep {e2:00}";
                }
            }

            return (ep, tag, bytes);
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

        #region helpers: HTTP-scrape
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

        #region Anti-bot retry helpers
        static bool IsAntiBotStatus(HttpStatusCode code)
        {
            return code == HttpStatusCode.Forbidden     // 403
                || code == (HttpStatusCode)429          // Too Many Requests
                || code == HttpStatusCode.ServiceUnavailable; // 503
        }

        static bool IsAntiBotHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;
            html = html.ToLowerInvariant();
            // Частые признаки: Cloudflare, DDoS protection, Attention Required
            return html.Contains("cloudflare")
                || html.Contains("ddos protection")
                || html.Contains("attention required")
                || html.Contains("restricted")
                || html.Contains("access denied");
        }

        async Task<string> GetPageHtmlWithRetries(string url, string referer = null, bool useproxy = false)
        {
            // 10 попыток с паузой 1 мин., затем 10 мин. пауза и последняя попытка.
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                var (ok, html) = await TryGetStringOnce(url, referer, 25, useproxy);
                if (ok && !IsAntiBotHtml(html))
                    return html;

                await Task.Delay(TimeSpan.FromMinutes(1));
            }

            await Task.Delay(TimeSpan.FromMinutes(10));

            {
                var (ok, html) = await TryGetStringOnce(url, referer, 25, useproxy);
                if (ok && !IsAntiBotHtml(html))
                    return html;
            }

            return null;
        }

        async Task<(bool ok, string html)> TryGetStringOnce(string url, string referer, int timeoutSeconds, bool useproxy)
        {
            try
            {
                var handler = new System.Net.Http.HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    AllowAutoRedirect = true,
                    // Proxy = ..., UseProxy = useproxy
                };
                using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Anistar.JacRed/1.6)");
                using var req = new System.Net.Http.HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(referer))
                    req.Headers.Referrer = new Uri(referer);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead);
                if (!resp.IsSuccessStatusCode)
                {
                    if (IsAntiBotStatus(resp.StatusCode)) return (false, null);
                    return (false, null);
                }
                var html = await resp.Content.ReadAsStringAsync();
                return (true, html);
            }
            catch
            {
                return (false, null);
            }
        }

        async Task<(byte[] data, string fileName)> FetchTorrentWithRetries(string url, string referer = null, bool useproxy = false)
        {
            // 10 попыток с паузой 1 мин., затем 10 мин. пауза и последняя попытка.
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                var res = await TryFetchTorrentOnce(url, referer, 30, useproxy);
                if (res.ok) return (res.data, res.fileName);

                await Task.Delay(TimeSpan.FromMinutes(1));
            }

            await Task.Delay(TimeSpan.FromMinutes(10));

            {
                var res = await TryFetchTorrentOnce(url, referer, 30, useproxy);
                if (res.ok) return (res.data, res.fileName);
            }

            return (null, null);
        }

        async Task<(bool ok, byte[] data, string fileName)> TryFetchTorrentOnce(string url, string referer, int timeoutSeconds, bool useproxy)
        {
            try
            {
                var handler = new System.Net.Http.HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    AllowAutoRedirect = true,
                };

                using var http = new System.Net.Http.HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(timeoutSeconds)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Anistar.JacRed/1.6)");

                using var req = new System.Net.Http.HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(referer))
                    req.Headers.Referrer = new Uri(referer);
                req.Headers.Accept.ParseAdd("application/x-bittorrent, application/octet-stream, */*");

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode)
                {
                    if (IsAntiBotStatus(resp.StatusCode)) return (false, null, null);
                    return (false, null, null);
                }

                string fileName = TryGetFileName(resp.Content?.Headers?.ContentDisposition)
                                  ?? TryGetFileNameFromHeaders(resp.Content?.Headers)
                                  ?? GetFileNameFromUrl(url);

                byte[] data = await resp.Content.ReadAsByteArrayAsync();
                if (data == null || data.Length == 0) return (false, null, null);

                return (true, data, fileName);
            }
            catch
            {
                return (false, null, null);
            }
        }
        #endregion

        #region BYTES FETCH (для scrape и пр. без ретраев в минуту)
        async Task<byte[]> FetchBytesAsync(string url, string referer = null, int timeoutSeconds = 20, bool useproxy = false)
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                // Proxy/UseProxy — при необходимости интегрируй с проектной прокси
            };

            using var http = new System.Net.Http.HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Anistar.JacRed/1.6)");

            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(referer))
                req.Headers.Referrer = new Uri(referer);

            req.Headers.Accept.ParseAdd("application/x-bittorrent, application/octet-stream, text/plain, */*");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync();
        }

        // Имя файла из Content-Disposition/headers/url
        static string TryGetFileName(ContentDispositionHeaderValue cd)
        {
            if (cd == null) return null;
            var f = cd.FileNameStar ?? cd.FileName;
            if (string.IsNullOrWhiteSpace(f)) return null;
            return f.Trim('\"');
        }

        static string TryGetFileNameFromHeaders(HttpContentHeaders headers)
        {
            if (headers == null) return null;
            if (headers.Contains("Content-Disposition"))
            {
                var raw = headers.GetValues("Content-Disposition").FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var m = Regex.Match(raw, @"filename\*?=""?([^\"";]+)""?", RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            return null;
        }

        static string GetFileNameFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var name = IO.Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(name)) return null;
                return name;
            }
            catch { return null; }
        }
        #endregion
    }
}
