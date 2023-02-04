using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace JacRed.Engine
{
    public static class StatsCron
    {
        async public static Task Run()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(AppInit.conf.timeStatsUpdate));

                try
                {
                    var stats = new Dictionary<string, (DateTime lastnewtor, int newtor, int update, int check, int alltorrents)>();

                    foreach (var item in FileDB.masterDb)
                    {
                        foreach (var t in FileDB.OpenRead(item.Key).Values)
                        {
                            if (!stats.TryGetValue(t.trackerName, out var val))
                                stats.Add(t.trackerName, (t.createTime, 0, 0, 0, 0));

                            var s = stats[t.trackerName];
                            s.alltorrents = s.alltorrents + 1;

                            if (t.createTime > s.lastnewtor)
                                s.lastnewtor = t.createTime;

                            if (t.createTime >= DateTime.Today)
                                s.newtor = s.newtor + 1;

                            if (t.updateTime >= DateTime.Today)
                                s.update = s.update + 1;

                            if (t.checkTime >= DateTime.Today)
                                s.check = s.check + 1;

                            stats[t.trackerName] = s;
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
                    }), Formatting.Indented));
                }
                catch { }
            }
        }
    }
}
