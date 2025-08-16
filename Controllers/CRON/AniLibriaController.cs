using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JacRed.Engine.CORE;
using Microsoft.AspNetCore.Mvc;
using JacRed.Models.tParse.AniLibria;
using JacRed.Engine;
using JacRed.Models.Details;
using System.Linq;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/anilibria/[action]")]
    public class AniLibriaController : BaseController
    {
        static bool workParse = false;

        async public Task<string> Parse(int limit)
        {
            if (workParse)
                return "work";

            workParse = true;

            if (limit == 0)
            {
                limit = 40;
            }

            try
            {
                // Use the modern AniLibria API v1 endpoint
                var apiUrl = $"{AppInit.conf.Anilibria.rqHost()}/v1/getUpdates";
                
                for (int after = 0; after <= limit; after = after + 40)
                {
                    // Build query parameters for the API
                    var queryParams = new List<string>
                    {
                        $"limit=40",
                        $"after={after}",
                        "include=raw_torrent"
                    };

                    var fullUrl = $"{apiUrl}?{string.Join("&", queryParams)}";
                    
                    var roots = await HttpClient.Get<List<RootObject>>(
                        fullUrl, 
                        IgnoreDeserializeObject: true, 
                        useproxy: AppInit.conf.Anilibria.useproxy
                    );

                    if (roots == null || roots.Count == 0)
                        continue;

                    foreach (var root in roots)
                    {
                        if (root == null || string.IsNullOrWhiteSpace(root.code))
                            continue;

                        var torrents = new List<TorrentBaseDetails>();
                        
                        // Use the most recent timestamp for creation time
                        DateTime createTime = new DateTime(1970, 1, 1, 0, 0, 0, 0)
                            .AddSeconds(Math.Max(root.last_change, root.updated));

                        if (root.torrents?.list == null || root.torrents.list.Count == 0)
                            continue;

                        foreach (var torrent in root.torrents.list)
                        {
                            if (torrent == null)
                                continue;

                            // Skip low quality torrents and those without proper data
                            if (ShouldSkipTorrent(root, torrent))
                                continue;

                            // Generate unique URL identifier
                            string url = GenerateTorrentUrl(root, torrent);
                            
                            // Generate title
                            string title = GenerateTorrentTitle(root, torrent);

                            // Process magnet link
                            var magnetData = ProcessMagnetLink(torrent);
                            if (magnetData == null)
                                continue;

                            torrents.Add(new TorrentBaseDetails()
                            {
                                trackerName = "anilibria",
                                types = new string[] { "anime" },
                                url = url,
                                title = title,
                                sid = torrent.seeders,
                                pir = torrent.leechers,
                                createTime = createTime,
                                magnet = magnetData.magnet,
                                sizeName = magnetData.sizeName,
                                name = tParse.ReplaceBadNames(root.names?.ru ?? ""),
                                originalname = tParse.ReplaceBadNames(root.names?.en ?? ""),
                                relased = root.season?.year ?? 0
                            });
                        }

                        if (torrents.Count > 0)
                        {
                            FileDB.AddOrUpdate(torrents);
                        }
                    }

                    roots = null;
                }
            }
            catch (Exception ex)
            {
                // Log error if logging is enabled
                if (AppInit.conf.log)
                {
                    Console.WriteLine($"AniLibria parse error: {ex.Message}");
                }
            }
            finally
            {
                workParse = false;
            }

            return "ok";
        }

        /// <summary>
        /// Search for anime titles using AniLibria API
        /// </summary>
        async public Task<string> Search(string query, int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "empty query";

            try
            {
                var apiUrl = $"{AppInit.conf.Anilibria.rqHost()}/v1/searchTitles";
                var queryParams = new List<string>
                {
                    $"search={Uri.EscapeDataString(query)}",
                    $"limit={limit}"
                };

                var fullUrl = $"{apiUrl}?{string.Join("&", queryParams)}";
                
                var results = await HttpClient.Get<List<RootObject>>(
                    fullUrl,
                    IgnoreDeserializeObject: true,
                    useproxy: AppInit.conf.Anilibria.useproxy
                );

                if (results == null || results.Count == 0)
                    return "no results";

                // Process search results similar to updates
                foreach (var root in results)
                {
                    if (root?.torrents?.list == null)
                        continue;

                    var torrents = new List<TorrentBaseDetails>();
                    DateTime createTime = new DateTime(1970, 1, 1, 0, 0, 0, 0)
                        .AddSeconds(Math.Max(root.last_change, root.updated));

                    foreach (var torrent in root.torrents.list)
                    {
                        if (ShouldSkipTorrent(root, torrent))
                            continue;

                        var magnetData = ProcessMagnetLink(torrent);
                        if (magnetData == null)
                            continue;

                        torrents.Add(new TorrentBaseDetails()
                        {
                            trackerName = "anilibria",
                            types = new string[] { "anime" },
                            url = GenerateTorrentUrl(root, torrent),
                            title = GenerateTorrentTitle(root, torrent),
                            sid = torrent.seeders,
                            pir = torrent.leechers,
                            createTime = createTime,
                            magnet = magnetData.magnet,
                            sizeName = magnetData.sizeName,
                            name = tParse.ReplaceBadNames(root.names?.ru ?? ""),
                            originalname = tParse.ReplaceBadNames(root.names?.en ?? ""),
                            relased = root.season?.year ?? 0
                        });
                    }

                    if (torrents.Count > 0)
                    {
                        FileDB.AddOrUpdate(torrents);
                    }
                }

                return $"processed {results.Count} results";
            }
            catch (Exception ex)
            {
                if (AppInit.conf.log)
                {
                    Console.WriteLine($"AniLibria search error: {ex.Message}");
                }
                return "error";
            }
        }

        /// <summary>
        /// Get specific title information by code
        /// </summary>
        async public Task<string> GetTitle(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return "empty code";

            try
            {
                var apiUrl = $"{AppInit.conf.Anilibria.rqHost()}/v1/getTitle";
                var fullUrl = $"{apiUrl}?code={Uri.EscapeDataString(code)}&include=raw_torrent";
                
                var root = await HttpClient.Get<RootObject>(
                    fullUrl,
                    IgnoreDeserializeObject: true,
                    useproxy: AppInit.conf.Anilibria.useproxy
                );

                if (root == null || root.torrents?.list == null)
                    return "title not found";

                var torrents = new List<TorrentBaseDetails>();
                DateTime createTime = new DateTime(1970, 1, 1, 0, 0, 0, 0)
                    .AddSeconds(Math.Max(root.last_change, root.updated));

                foreach (var torrent in root.torrents.list)
                {
                    if (ShouldSkipTorrent(root, torrent))
                        continue;

                    var magnetData = ProcessMagnetLink(torrent);
                    if (magnetData == null)
                        continue;

                    torrents.Add(new TorrentBaseDetails()
                    {
                        trackerName = "anilibria",
                        types = new string[] { "anime" },
                        url = GenerateTorrentUrl(root, torrent),
                        title = GenerateTorrentTitle(root, torrent),
                        sid = torrent.seeders,
                        pir = torrent.leechers,
                        createTime = createTime,
                        magnet = magnetData.magnet,
                        sizeName = magnetData.sizeName,
                        name = tParse.ReplaceBadNames(root.names?.ru ?? ""),
                        originalname = tParse.ReplaceBadNames(root.names?.en ?? ""),
                        relased = root.season?.year ?? 0
                    });
                }

                if (torrents.Count > 0)
                {
                    FileDB.AddOrUpdate(torrents);
                    return $"processed {torrents.Count} torrents for {code}";
                }

                return "no valid torrents found";
            }
            catch (Exception ex)
            {
                if (AppInit.conf.log)
                {
                    Console.WriteLine($"AniLibria getTitle error: {ex.Message}");
                }
                return "error";
            }
        }

        private bool ShouldSkipTorrent(RootObject root, Torrent torrent)
        {
            // Skip if no code or low quality torrents
            if (string.IsNullOrWhiteSpace(root.code))
                return true;

            // Skip low resolution torrents (480p and below) without encoder info
            if (torrent.quality?.resolution <= 480 && 
                string.IsNullOrWhiteSpace(torrent.quality?.encoder) && 
                string.IsNullOrWhiteSpace(torrent.url))
                return true;

            // Skip torrents without raw torrent data
            if (string.IsNullOrWhiteSpace(torrent.raw_base64_file))
                return true;

            return false;
        }

        private string GenerateTorrentUrl(RootObject root, Torrent torrent)
        {
            return $"anilibria.tv:{root.code}:{torrent.quality?.resolution}:{torrent.quality?.encoder}";
        }

        private string GenerateTorrentTitle(RootObject root, Torrent torrent)
        {
            var ruName = root.names?.ru ?? "";
            var enName = root.names?.en ?? "";
            var year = root.season?.year ?? 0;
            var seasonCode = root.season?.code ?? "";
            var episode = torrent.series?.@string ?? "";
            var quality = torrent.quality?.@string ?? "";

            return $"{ruName} / {enName} {year} (s{seasonCode}, e{episode}) [{quality}]";
        }

        private (string magnet, string sizeName)? ProcessMagnetLink(Torrent torrent)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(torrent.raw_base64_file))
                    return null;

                byte[] torrentData = Convert.FromBase64String(torrent.raw_base64_file);
                string magnet = BencodeTo.Magnet(torrentData);
                string sizeName = BencodeTo.SizeName(torrentData);

                if (string.IsNullOrWhiteSpace(magnet) || string.IsNullOrWhiteSpace(sizeName))
                    return null;

                return (magnet, sizeName);
            }
            catch
            {
                return null;
            }
        }
    }
}