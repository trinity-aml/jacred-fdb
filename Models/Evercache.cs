namespace JacRed.Models
{
    public class Evercache
    {
        public bool enable { get; set; }

        public int validHour { get; set; }

        public int maxOpenWriteTask { get; set; }

        public int dropCacheTake { get; set; }
    }
}
