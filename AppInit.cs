using JacRed.Models.AppConf;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace JacRed
{
    public class AppInit
    {
        #region AppInit
        static (AppInit, DateTime) cacheconf = default;

        public static AppInit conf
        {
            get
            {
                if (cacheconf.Item1 == null)
                {
                    if (!File.Exists("init.conf"))
                        return new AppInit();
                }

                var lastWriteTime = File.GetLastWriteTime("init.conf");

                if (cacheconf.Item2 != lastWriteTime)
                {
                    cacheconf.Item1 = JsonConvert.DeserializeObject<AppInit>(File.ReadAllText("init.conf"));
                    cacheconf.Item2 = lastWriteTime;
                }

                return cacheconf.Item1;
            }
        }
        #endregion


        public string listenip = "any";

        public int listenport = 9117;

        public string apikey = null;

        public bool mergeduplicates = true;

        public bool openstats = true;

        public bool opensync = false;

        public bool log = false;

        public string syncapi = null;

        public string[] synctrackers = null;

        public int maxreadfile = 100;

        public bool evercache = false;

        public int fdbPathLevels = 2;

        public int timeStatsUpdate = 20; // минут

        public int timeSync = 10; // минут


        public TrackerSettings Rutor = new TrackerSettings("http://rutor.info");

        public TrackerSettings Megapeer = new TrackerSettings("http://megapeer.vip");

        public TrackerSettings TorrentBy = new TrackerSettings("https://torrent.by");

        public TrackerSettings Kinozal = new TrackerSettings("https://kinozal.tv");

        public TrackerSettings NNMClub = new TrackerSettings("https://nnmclub.to");

        public TrackerSettings Bitru = new TrackerSettings("https://bitru.org");

        public TrackerSettings Toloka = new TrackerSettings("https://toloka.to");

        public TrackerSettings Rutracker = new TrackerSettings("https://rutracker.net");

        public TrackerSettings Selezen = new TrackerSettings("https://open.selezen.org");

        public TrackerSettings Anilibria = new TrackerSettings("https://api.anilibria.tv");

        public TrackerSettings Animelayer = new TrackerSettings("http://animelayer.ru");

        public TrackerSettings Anidub = new TrackerSettings("https://tr.anidub.com");

        public TrackerSettings Anifilm = new TrackerSettings("https://anifilm.tv");

        public TrackerSettings Rezka = new TrackerSettings("https://rezka.cc");

        public TrackerSettings Baibako = new TrackerSettings("http://baibako.tv");

        public TrackerSettings Lostfilm = new TrackerSettings("https://www.lostfilm.tv");


        public ProxySettings proxy = new ProxySettings();

        public List<ProxySettings> globalproxy = new List<ProxySettings>()
        {
            new ProxySettings()
            {
                pattern = "\\.onion",
                list = new List<string>() { "socks5://127.0.0.1:9050" }
            }
        };
    }
}
