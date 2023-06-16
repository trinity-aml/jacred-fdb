using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using JacRed.Engine.CORE;
using JacRed.Models;
using JacRed.Models.Details;

namespace JacRed.Engine
{
    public partial class FileDB : IDisposable
    {
        #region FileDB
        /// <summary>
        /// $"{search_name}:{search_originalname}"
        /// Верхнее время изменения 
        /// </summary>
        public static ConcurrentDictionary<string, DateTime> masterDb = new ConcurrentDictionary<string, DateTime>();

        static ConcurrentDictionary<string, WriteTaskModel> openWriteTask = new ConcurrentDictionary<string, WriteTaskModel>();

        static FileDB()
        {
            if (File.Exists("Data/masterDb.bz"))
                masterDb = JsonStream.Read<ConcurrentDictionary<string, DateTime>>("Data/masterDb.bz");
        }
        #endregion

        #region pathDb / keyDb
        static string pathDb(string key)
        {
            string md5key = HashTo.md5(key);

            if (AppInit.conf.fdbPathLevels == 2)
            {
                Directory.CreateDirectory($"Data/fdb/{md5key.Substring(0, 2)}");
                return $"Data/fdb/{md5key.Substring(0, 2)}/{md5key.Substring(2)}";
            }
            else
            {
                Directory.CreateDirectory($"Data/fdb/{md5key[0]}");
                return $"Data/fdb/{md5key[0]}/{md5key}";
            }
        }

        static string keyDb(string name, string originalname)
        {
            string search_name = StringConvert.SearchName(name);
            string search_originalname = StringConvert.SearchName(originalname);
            return $"{search_name}:{search_originalname}";
        }
        #endregion

        #region AddOrUpdateMasterDb
        static void AddOrUpdateMasterDb(TorrentDetails torrent)
        {
            string key = keyDb(torrent.name, torrent.originalname);

            if (masterDb.TryGetValue(key, out DateTime updateTime))
            {
                if (torrent.updateTime > updateTime)
                    masterDb[key] = torrent.updateTime;
            }
            else
            {
                masterDb.TryAdd(key, torrent.updateTime);
            }
        }
        #endregion

        #region OpenRead / OpenWrite
        public static IReadOnlyDictionary<string, TorrentDetails> OpenRead(string key)
        {
            if (openWriteTask.TryGetValue(key, out WriteTaskModel val))
                return val.db.Database;

            if (AppInit.conf.evercache)
            {
                var fdb = new FileDB(key);
                openWriteTask.TryAdd(key, new WriteTaskModel() { db = fdb, openconnection = 1 });
                return fdb.Database;
            }

            return new FileDB(key).Database;
        }

        public static FileDB OpenWrite(string key)
        {
            if (openWriteTask.TryGetValue(key, out WriteTaskModel val))
            {
                val.openconnection += 1;
                return val.db;
            }
            else
            {
                var fdb = new FileDB(key);
                openWriteTask.TryAdd(key, new WriteTaskModel() { db = fdb, openconnection = 1 });
                return fdb;
            }
        }
        #endregion

        #region AddOrUpdate
        public static void AddOrUpdate(IReadOnlyCollection<TorrentBaseDetails> torrents)
        {
            _ = AddOrUpdate(torrents, null);
        }

        async public static ValueTask AddOrUpdate<T>(IReadOnlyCollection<T> torrents, Func<T, IReadOnlyDictionary<string, TorrentDetails>, Task<bool>> predicate) where T : TorrentBaseDetails
        {
            var temp = new Dictionary<string, List<T>>();

            foreach (var torrent in torrents)
            {
                string key = keyDb(torrent.name, torrent.originalname);
                if (!temp.ContainsKey(key))
                    temp.Add(key, new List<T>());

                temp[key].Add(torrent);
            }

            foreach (var t in temp)
            {
                using (var fdb = OpenWrite(t.Key))
                {
                    foreach (var torrent in t.Value)
                    {
                        if (predicate != null)
                        {
                            if (await predicate.Invoke(torrent, fdb.Database) == false)
                                continue;
                        }

                        fdb.AddOrUpdate(torrent);
                    }
                }
            }
        }
        #endregion

        #region SaveChangesToFile
        public static void SaveChangesToFile()
        {
            try
            {
                JsonStream.Write("Data/masterDb.bz", masterDb);

                if (!File.Exists($"Data/masterDb_{DateTime.Today:dd-MM-yyyy}.bz"))
                    File.Copy("Data/masterDb.bz", $"Data/masterDb_{DateTime.Today:dd-MM-yyyy}.bz");

                if (File.Exists($"Data/masterDb_{DateTime.Today.AddDays(-3):dd-MM-yyyy}.bz"))
                    File.Delete($"Data/masterDb_{DateTime.Today.AddDays(-3):dd-MM-yyyy}.bz");
            }
            catch { }
        }
        #endregion
    }
}
