﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Aggregates.Contracts;
using Aggregates.Extensions;
using NServiceBus;

namespace Aggregates.Internal
{
    class NSBPublisher : IMessagePublisher
    {

        public async Task Publish<T>(string streamName, IEnumerable<IFullEvent> events, IDictionary<string, string> commitHeaders) where T : IEventSource
        {
            await events.WhenAllAsync(@event =>
            {
                var options = new PublishOptions();

                foreach (var header in commitHeaders)
                {
                    if (header.Key == Headers.OriginatingHostId)
                    {
                        //is added by bus in v5
                        continue;
                    }
                    options.SetHeader(header.Key, header.Value);
                }

                options.SetHeader($"{Defaults.PrefixHeader}.EventId", @event.EventId.ToString());
                options.SetHeader($"{Defaults.PrefixHeader}.EntityType", @event.Descriptor.EntityType);
                options.SetHeader($"{Defaults.PrefixHeader}.Timestamp", @event.Descriptor.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                options.SetHeader($"{Defaults.PrefixHeader}.Version", @event.Descriptor.Version.ToString());

                options.SetHeader($"{Defaults.PrefixHeader}.EventStream", streamName);

                foreach (var header in @event.Descriptor.Headers)
                    options.SetHeader(header.Key, header.Value);


                return Bus.Instance.Publish(@event.Event, options);
            }).ConfigureAwait(false);

        }
    }
}
