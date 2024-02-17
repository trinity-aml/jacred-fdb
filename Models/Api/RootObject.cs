using System.Collections.Generic;

namespace JacRed.Models.Api
{
    public class RootObject
    {
        public List<Result> Results { get; set; }

        public bool jacred => true;
    }
}
