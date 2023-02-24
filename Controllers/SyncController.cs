using System;
using System.Collections.Generic;
using System.Linq;
using JacRed.Engine;
using JacRed.Models.Details;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace JacRed.Controllers
{
    public class SyncController : Controller
    {
        [Route("/sync/conf")]
        public JsonResult Configuration()
        {
            return Json(new 
            {
                fbd = true,
                version = 2
            });
        }

        [Route("/sync/fdb/torrents")]
        public ActionResult FdbTorrents(long time)
        {
            if (!AppInit.conf.opensync || time == 0)
                return Json(new List<string>());

            bool nextread = false;
            int take = 5_000, countread = 0;
            DateTime lastsync = time == -1 ? default : DateTime.FromFileTimeUtc(time);

            var torrents = new Dictionary<string, (DateTime, IReadOnlyDictionary<string, TorrentDetails>)>();
            foreach (var item in FileDB.masterDb.OrderBy(i => i.Value).Where(i => i.Value > lastsync))
            {
                var torrent = FileDB.OpenRead(item.Key);
                countread = countread + torrent.Count;

                torrents.TryAdd(item.Key, (item.Value, torrent));

                if (countread > take)
                {
                    nextread = true;
                    break;
                }
            }

            return Content(JsonConvert.SerializeObject(new { nextread, collections = torrents.OrderBy(i => i.Value.Item1).Select(i => new 
            {
                i.Key,
                Value = new 
                {
                    time = i.Value.Item1,
                    torrents = i.Value.Item2
                }
            })}));
        }


        [Route("/sync/torrents")]
        public JsonResult Torrents(long time)
        {
            if (!AppInit.conf.opensync || time == 0)
                return Json(new List<string>());

            int take = 5_000;
            DateTime lastsync = time == -1 ? default : DateTime.FromFileTimeUtc(time);

            var torrents = new Dictionary<string, TorrentDetails>();
            foreach (var item in FileDB.masterDb.OrderBy(i => i.Value).Where(i => i.Value > lastsync))
            {
                foreach (var torrent in FileDB.OpenRead(item.Key))
                {
                    if (torrents.TryGetValue(torrent.Key, out TorrentDetails val))
                    {
                        if (torrent.Value.updateTime > val.updateTime)
                            torrents[torrent.Key] = torrent.Value;
                    }
                    else
                    {
                        torrents.TryAdd(torrent.Key, torrent.Value);
                    }
                }

                if (torrents.Count > take)
                {
                    take = torrents.Count;
                    break;
                }
            }

            return Json(new { take, torrents = torrents.OrderBy(i => i.Value.updateTime) });
        }
    }
}
