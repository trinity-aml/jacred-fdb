using JacRed.Engine.CORE;
using JacRed.Models.Details;
using JacRed.Models.Sync;
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
        static long lastsync = -1;

        async public static Task Run()
        {
            while (true)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(AppInit.conf.syncapi))
                    {
                        if (lastsync == -1 && File.Exists("lastsync.txt"))
                            lastsync = long.Parse(File.ReadAllText("lastsync.txt"));

                        var conf = await HttpClient.Get<JObject>($"{AppInit.conf.syncapi}/sync/conf");
                        if (conf != null && conf.ContainsKey("fbd") && conf.Value<bool>("fbd"))
                        {
                            #region Sync.v2
                            var root = await HttpClient.Get<Models.Sync.v2.RootObject>($"{AppInit.conf.syncapi}/sync/fdb/torrents?time={lastsync}", timeoutSeconds: 300, MaxResponseContentBufferSize: 100_000_000);
                            if (root?.collections != null && root.collections.Count > 0)
                            {
                                var torrents = new List<TorrentBaseDetails>();

                                foreach (var collection in root.collections)
                                {
                                    foreach (var torrent in collection.Value.torrents)
                                    {
                                        if (torrent.Value.types == null)
                                            continue;

                                        if (AppInit.conf.synctrackers != null && !AppInit.conf.synctrackers.Contains(torrent.Value.trackerName))
                                            continue;

                                        torrents.Add(torrent.Value);
                                    }
                                }

                                FileDB.AddOrUpdate(torrents);

                                lastsync = root.collections.Last().Value.time.ToFileTimeUtc();

                                if (root.nextread)
                                    continue;
                            }
                            #endregion
                        }
                        else
                        {
                            #region Sync.v1
                            var root = await HttpClient.Get<RootObject>($"{AppInit.conf.syncapi}/sync/torrents?time={lastsync}", timeoutSeconds: 300, MaxResponseContentBufferSize: 100_000_000);
                            if (root?.torrents != null && root.torrents.Count > 0)
                            {
                                FileDB.AddOrUpdate(root.torrents.Select(i => i.value).ToList());

                                lastsync = root.torrents.Last().value.updateTime.ToFileTimeUtc();

                                if (root.take == root.torrents.Count)
                                    continue;
                            }
                            #endregion
                        }

                        FileDB.SaveChangesToFile();
                        File.WriteAllText("lastsync.txt", lastsync.ToString());
                    }
                }
                catch { }

                await Task.Delay(TimeSpan.FromMinutes(AppInit.conf.timeSync));
            }
        }
    }
}
