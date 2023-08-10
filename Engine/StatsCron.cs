using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace JacRed.Engine
{
    public static class StatsCron
    {
        async public static Task Run()
        {
            await Task.Delay(20_000);

            while (true)
            {
                try
                {
                    var today = DateTime.Today - (DateTime.Now - DateTime.UtcNow);
                    var stats = new Dictionary<string, (DateTime lastnewtor, int newtor, int update, int check, int alltorrents, int trkconfirm, int trkwait)>();

                    foreach (var item in FileDB.masterDb.ToArray())
                    {
                        foreach (var t in FileDB.OpenRead(item.Key).Values)
                        {
                            if (string.IsNullOrEmpty(t.trackerName))
                                continue;

                            try
                            {
                                if (!stats.TryGetValue(t.trackerName, out var val))
                                    stats.Add(t.trackerName, (t.createTime, 0, 0, 0, 0, 0, 0));

                                var s = stats[t.trackerName];
                                s.alltorrents = s.alltorrents + 1;

                                if (t.createTime > s.lastnewtor)
                                    s.lastnewtor = t.createTime;

                                if (t.createTime >= today)
                                    s.newtor = s.newtor + 1;

                                if (t.updateTime >= today)
                                    s.update = s.update + 1;

                                if (t.checkTime >= today)
                                    s.check = s.check + 1;

                                if (AppInit.conf.tracks && !TracksDB.theBad(t.types))
                                {
                                    if (TracksDB.Get(t.magnet) != null)
                                        s.trkconfirm = s.trkconfirm + 1;
                                    else
                                        s.trkwait = s.trkwait + 1;
                                }

                                stats[t.trackerName] = s;
                            }
                            catch { }
                        }
                    }

                    File.WriteAllText("Data/temp/stats.json", JsonConvert.SerializeObject(stats.OrderByDescending(i => i.Value.alltorrents).Select(i => new
                    {
                        trackerName = i.Key,
                        lastnewtor = i.Value.lastnewtor.ToString("dd.MM.yyyy"),
                        i.Value.newtor,
                        i.Value.update,
                        i.Value.check,
                        i.Value.alltorrents,
                        tracks = new 
                        {
                            wait = i.Value.trkwait,
                            confirm = i.Value.trkconfirm
                        }

                    }), Formatting.Indented));
                }
                catch { }

                await Task.Delay(TimeSpan.FromMinutes(AppInit.conf.timeStatsUpdate));
            }
        }
    }
}
