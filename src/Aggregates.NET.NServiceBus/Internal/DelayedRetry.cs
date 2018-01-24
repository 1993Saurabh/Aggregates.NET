﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Aggregates.Contracts;
using NServiceBus;

namespace Aggregates.Internal
{
    class DelayedRetry
    {
        private readonly IMetrics _metrics;
        private readonly IMessageDispatcher _dispatcher;
        private static readonly Dictionary<string, Task> Tasks = new Dictionary<string, Task>();

        public DelayedRetry(IMetrics metrics, IMessageDispatcher dispatcher)
        {
            _metrics = metrics;
            _dispatcher = dispatcher;
        }

        public void QueueRetry(IFullMessage message, TimeSpan delay)
        {
            _metrics.Increment("Retry Queue", Unit.Message);
            var messageId = Guid.NewGuid().ToString();
            message.Headers.TryGetValue(Headers.MessageId, out messageId);

            Tasks[messageId] = Timer.Expire((state) =>
            {
                var msg = (IFullMessage)state;

                Tasks.Remove(messageId);
                _metrics.Decrement("Retry Queue", Unit.Message);
                return _dispatcher.SendLocal(msg);
            }, message, delay, $"message {messageId}");
        }        
    }
}
