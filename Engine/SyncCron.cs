﻿using JacRed.Engine.CORE;
using JacRed.Models.Details;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace JacRed.Engine
{
    public static class SyncCron
    {
        static long lastsync = -1, starsync = -1;

        #region Torrents
        async public static Task Torrents()
        {
            await Task.Delay(20_000);

            while (true)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(AppInit.conf.syncapi))
                    {
                        Console.WriteLine($"\n\nsync: start / {DateTime.Now}");

                        if (lastsync == -1 && File.Exists("lastsync.txt"))
                            lastsync = long.Parse(File.ReadAllText("lastsync.txt"));

                        var conf = await HttpClient.Get<JObject>($"{AppInit.conf.syncapi}/sync/conf");
                        if (conf != null && conf.ContainsKey("fbd") && conf.Value<bool>("fbd"))
                        {
                            #region Sync.v2
                            if (starsync == -1 && File.Exists("starsync.txt"))
                                starsync = long.Parse(File.ReadAllText("starsync.txt"));

                            bool reset = true;
                            DateTime lastSave = DateTime.Now;

                            next: var root = await HttpClient.Get<Models.Sync.v2.RootObject>($"{AppInit.conf.syncapi}/sync/fdb/torrents?time={lastsync}&start={starsync}", timeoutSeconds: 300, MaxResponseContentBufferSize: 100_000_000);

                            Console.WriteLine($"sync: time={lastsync}&start={starsync}");

                            if (root?.collections == null)
                            {
                                if (reset)
                                {
                                    reset = false;
                                    await Task.Delay(TimeSpan.FromMinutes(1));
                                    goto next;
                                }
                            }
                            else if (root.collections.Count > 0)
                            {
                                reset = true;
                                var torrents = new List<TorrentBaseDetails>(root.countread);

                                foreach (var collection in root.collections)
                                {
                                    foreach (var torrent in collection.Value.torrents)
                                    {
                                        if (AppInit.conf.synctrackers != null && torrent.Value.trackerName != null && !AppInit.conf.synctrackers.Contains(torrent.Value.trackerName))
                                            continue;

                                        if (!AppInit.conf.syncsport && torrent.Value.types != null && torrent.Value.types.Contains("sport"))
                                            continue;

                                        torrents.Add(torrent.Value);
                                    }
                                }

                                FileDB.AddOrUpdate(torrents);

                                lastsync = root.collections.Last().Value.fileTime;

                                if (root.nextread)
                                {
                                    if (DateTime.Now > lastSave.AddMinutes(5))
                                    {
                                        lastSave = DateTime.Now;
                                        FileDB.SaveChangesToFile();
                                        File.WriteAllText("lastsync.txt", lastsync.ToString());
                                    }

                                    goto next;
                                }

                                starsync = lastsync;
                                File.WriteAllText("starsync.txt", starsync.ToString());
                            }
                            else if (root.collections.Count == 0)
                            {
                                starsync = lastsync;
                                File.WriteAllText("starsync.txt", starsync.ToString());
                            }
                            #endregion
                        }
                        else
                        {
                            #region Sync.v1
                            next: var root = await HttpClient.Get<Models.Sync.v1.RootObject>($"{AppInit.conf.syncapi}/sync/torrents?time={lastsync}", timeoutSeconds: 300, MaxResponseContentBufferSize: 100_000_000);
                            if (root?.torrents != null && root.torrents.Count > 0)
                            {
                                FileDB.AddOrUpdate(root.torrents.Select(i => i.value).ToList());

                                lastsync = root.torrents.Last().value.updateTime.ToFileTimeUtc();

                                if (root.take == root.torrents.Count)
                                    goto next;
                            }
                            #endregion
                        }

                        FileDB.SaveChangesToFile();
                        File.WriteAllText("lastsync.txt", lastsync.ToString());

                        Console.WriteLine("sync: end");
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1));
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (lastsync > 0)
                        {
                            FileDB.SaveChangesToFile();
                            File.WriteAllText("lastsync.txt", lastsync.ToString());
                        }
                    }
                    catch { }

                    Console.WriteLine("sync: error / " + ex.Message);
                }

                await Task.Delay(1000 * Random.Shared.Next(60, 300));
                await Task.Delay(1000 * 60 * (20 > AppInit.conf.timeSync ? 20 : AppInit.conf.timeSync));
            }
        }
        #endregion

        #region Spidr
        async public static Task Spidr()
        {
            while (true)
            {
                await Task.Delay(1000 * Random.Shared.Next(60, 300));
                await Task.Delay(1000 * 60 * 120);

                try
                {
                    if (!string.IsNullOrWhiteSpace(AppInit.conf.syncapi) && AppInit.conf.syncspidr)
                    {
                        long lastsync_spidr = -1;

                        var conf = await HttpClient.Get<JObject>($"{AppInit.conf.syncapi}/sync/conf");
                        if (conf != null && conf.ContainsKey("spidr") && conf.Value<bool>("spidr"))
                        {
                            Console.WriteLine($"\n\nsync_spidr: start / {DateTime.Now}");

                            next: var root = await HttpClient.Get<Models.Sync.v2.RootObject>($"{AppInit.conf.syncapi}/sync/fdb/torrents?time={lastsync_spidr}&spidr=true", timeoutSeconds: 300, MaxResponseContentBufferSize: 100_000_000);

                            Console.WriteLine($"sync_spidr: time={lastsync_spidr}");

                            if (root?.collections != null && root.collections.Count > 0)
                            {
                                foreach (var collection in root.collections)
                                    FileDB.AddOrUpdate(collection.Value.torrents.Values);

                                lastsync_spidr = root.collections.Last().Value.fileTime;

                                if (root.nextread)
                                    goto next;
                            }

                            Console.WriteLine("sync_spidr: end");
                        }
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1));
                        continue;
                    }
                }
                catch (Exception ex) { Console.WriteLine("sync_spidr: error / " + ex.Message); }
            }
        }
        #endregion
    }
}