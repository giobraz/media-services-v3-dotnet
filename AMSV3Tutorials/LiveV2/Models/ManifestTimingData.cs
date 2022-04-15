using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveV2.Models
{
    public class ManifestTimingData
    {
        public TimeSpan AssetDuration { get; set; }
        public ulong TimestampOffset { get; set; }
        public ulong? TimeScale { get; set; }
        public bool IsLive { get; set; }
        public bool Error { get; set; }
        public List<ulong> TimestampList { get; set; }
        public ulong TimestampEndLastChunk { get; set; }
        public bool DiscontinuityDetected { get; set; }
    }
}
