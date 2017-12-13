﻿using System;
using Aggregates.Contracts;

namespace Aggregates.Internal
{
    public class Snapshot : ISnapshot
    {
        public string Bucket { get; set; }
        public Id StreamId { get; set; }
        public long Version { get; set; }
        public IState Payload { get; set; }

        public string EntityType { get; set; }
        public DateTime Timestamp { get; set; }
    }
}