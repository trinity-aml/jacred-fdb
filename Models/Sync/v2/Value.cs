using JacRed.Models.Details;
using System;
using System.Collections.Generic;

namespace JacRed.Models.Sync.v2
{
    public class Value
    {
        public DateTime time { get; set; }

        public long fileTime { get; set; }

        public Dictionary<string, TorrentDetails> torrents { get; set; }
    }
}
