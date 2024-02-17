using JacRed.Models.Details;

namespace JacRed.Models.Sync.v1
{
    public class Torrent
    {
        public string key { get; set; }

        public TorrentDetails value { get; set; }
    }
}
