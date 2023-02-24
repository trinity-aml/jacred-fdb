using System.Collections.Generic;

namespace JacRed.Models.Sync.v2
{
    public class RootObject
    {
        public bool nextread { get; set; }

        public List<Collection> collections { get; set; }
    }
}
