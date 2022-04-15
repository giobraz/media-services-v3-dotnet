using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveV2.Models
{
    public class SubclipInfo
    {
        public TimeSpan subclipStart { get; set; }
        public TimeSpan subclipDuration { get; set; }
        public string programId { get; set; }
    }
}
