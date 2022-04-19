using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveV2.Models
{
    class EndTimeInTable : ITableEntity
    {
        public string PartitionKey { get; set; }

        public string RowKey { get; set; } = "lastEndTime";

        public DateTimeOffset? Timestamp { get; set; }

        public ETag ETag { get; set; }



        public string LiveEventId { get; set; }

        public string Id { get; set; }

        public string LastEndTime { get; set; }

        public string LiveEventState { get; set; }

        public void AssignRowKey()
        {
            this.RowKey = "lastEndTime";
        }
        public void AssignPartitionKey()
        {
            this.PartitionKey = LiveEventId;
        }
    }
}
