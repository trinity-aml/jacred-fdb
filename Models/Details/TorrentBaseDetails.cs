﻿using JacRed.Models.Tracks;
using System;
using System.Collections.Generic;

namespace JacRed.Models.Details
{
    public class TorrentBaseDetails
    {
        public string trackerName { get; set; }

        public string[] types { get; set; }

        public string url { get; set; }


        public string title { get; set; }

        public int sid { get; set; }

        public int pir { get; set; }

        public string sizeName { get; set; }

        public DateTime createTime { get; set; } = DateTime.UtcNow;

        public DateTime updateTime { get; set; } = DateTime.UtcNow;

        public DateTime checkTime { get; set; } = DateTime.Now;

        public string magnet { get; set; }


        public string name { get; set; }

        public string originalname { get; set; }

        public int relased { get; set; }


        public HashSet<string> languages { get; set; }

        public List<ffStream> ffprobe { get; set; }
    }
}
