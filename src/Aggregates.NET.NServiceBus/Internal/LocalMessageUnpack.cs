﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aggregates.Contracts;
using Aggregates.Extensions;
using Aggregates.Logging;
using NServiceBus;
using NServiceBus.Pipeline;

namespace Aggregates.Internal
{
    class LocalMessageUnpack : Behavior<IIncomingLogicalMessageContext>
    {
        private static readonly ILog Logger = LogProvider.GetLogger("LocalMessageUnpack");

        private readonly IMetrics _metrics;

        public LocalMessageUnpack(IMetrics metrics)
        {
            _metrics = metrics;
        }

        public override async Task Invoke(IIncomingLogicalMessageContext context, Func<Task> next)
        {
            var originalheaders = new Dictionary<string, string>(context.Headers);
            var originalInstance = context.Message.Instance;

            // Stupid hack to get events from ES and messages from NSB into the same pipeline
            // Special case for delayed messages read from delayed stream
            if (context.Extensions.TryGet(Defaults.BulkHeader, out IFullMessage[] delayedMessages))
            {
                _metrics.Mark("Messages", Unit.Message, delayedMessages.Length);

                Logger.DebugEvent("Bulk", "Processing {Count}", delayedMessages.Length);
                var index = 1;

                try
                {
                    foreach (var x in delayedMessages)
                    {
                        // Replace all headers with the original headers to preserve CorrId etc.
                        context.Headers.Clear();
                        foreach (var header in x.Headers)
                            context.Headers[$"{Defaults.DelayedPrefixHeader}.{header.Key}"] = header.Value;

                        if (x.Headers.ContainsKey(Headers.MessageId))
                            context.Headers[Headers.MessageId] = x.Headers[Headers.MessageId];
                        if (x.Headers.ContainsKey(Headers.CorrelationId))
                            context.Headers[Headers.CorrelationId] = x.Headers[Headers.CorrelationId];
                        context.Headers[Defaults.BulkHeader] = delayedMessages.Length.ToString();
                        // Don't set on headers because headers are kept with the message through retries, could lead to unexpected results
                        context.Extensions.Set(Defaults.ChannelKey, x.Headers[Defaults.ChannelKey]);

                        context.UpdateMessageInstance(x.Message);
                        await next().ConfigureAwait(false);
                        index++;
                    }
                }
                finally
                {
                    // Restore original message headers
                    context.Headers.Clear();
                    foreach (var original in originalheaders)
                        context.Headers[original.Key] = original.Value;
                    context.UpdateMessageInstance(originalInstance);
                }
            }
            else if (context.Message.MessageType == typeof(BulkMessage))
            {
                var bulk = context.Message.Instance as BulkMessage;
                // A bulk message thats retried will be in extensions LocalHeader
                if (context.Extensions.TryGet(Defaults.LocalHeader, out object local))
                    bulk = local as BulkMessage;

                _metrics.Mark("Messages", Unit.Message, bulk.Messages.Length);
                Logger.DebugEvent("Bulk", "Processing {Count} [{MessageId:l}]", bulk.Messages.Length, context.MessageId);

                var index = 1;
                try
                {
                    foreach (var x in bulk.Messages)
                    {
                        // Replace all headers with the original headers to preserve CorrId etc.
                        context.Headers.Clear();
                        foreach (var header in x.Headers)
                            context.Headers[$"{Defaults.BulkPrefixHeader}.{header.Key}"] = header.Value;

                        if (x.Headers.ContainsKey(Headers.MessageId))
                            context.Headers[Headers.MessageId] = x.Headers[Headers.MessageId];
                        if (x.Headers.ContainsKey(Headers.CorrelationId))
                            context.Headers[Headers.CorrelationId] = x.Headers[Headers.CorrelationId];
                        context.Headers[Defaults.BulkHeader] = bulk.Messages.Length.ToString();

                        context.UpdateMessageInstance(x.Message);
                        await next().ConfigureAwait(false);
                        index++;
                    }
                }
                finally
                {
                    // Restore original message headers
                    context.Headers.Clear();
                    foreach (var original in originalheaders)
                        context.Headers[original.Key] = original.Value;
                    context.UpdateMessageInstance(originalInstance);
                }
            }
            else if (context.Extensions.TryGet(Defaults.LocalHeader, out object @event))
            {
                try
                {
                    _metrics.Mark("Messages", Unit.Message);
                    context.UpdateMessageInstance(@event);
                    await next().ConfigureAwait(false);
                }
                finally
                {
                    context.UpdateMessageInstance(originalInstance);
                }
            }
            else
            {
                _metrics.Mark("Messages", Unit.Message);
                await next().ConfigureAwait(false);
            }
        }
    }
    internal class LocalMessageUnpackRegistration : RegisterStep
    {
        public LocalMessageUnpackRegistration(IContainer container) : base(
            stepId: "LocalMessageUnpack",
            behavior: typeof(LocalMessageUnpack),
            description: "Pulls local message from context",
            factoryMethod: (b) => new LocalMessageUnpack(container.Resolve<IMetrics>())
        )
        {
            InsertAfterIfExists("UnitOfWorkExecution");
        }
    }
}
