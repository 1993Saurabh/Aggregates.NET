﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Aggregates.Contracts;
using NServiceBus.MessageInterfaces;
using NServiceBus.Settings;

namespace Aggregates.Internal
{
    /// <summary>
    /// Small wrapper around NSB's serializer interface to direct NSB to use w/e serializer aggregates is configured with
    /// (necessary because our serializers have special things we need to do and its hard to get those things into NSB's 
    /// serializer without breaking the library apart into "Aggregates.NET.NServiceBus.JsonNet" and "Aggregates.NET.NServiceBus.Protobuf")
    /// </summary>
    class Serializer : NServiceBus.Serialization.IMessageSerializer
    {
        private readonly IMessageSerializer _serializer;

        public string ContentType => "Json";

        public Serializer(IMessageSerializer serializer)
        {
            _serializer = serializer;
        }

        public object[] Deserialize(Stream stream, IList<Type> messageTypes = null)
        {
            return _serializer.Deserialize(stream, messageTypes);
        }

        public void Serialize(object message, Stream stream)
        {
            _serializer.Serialize(message, stream);
        }
    }

    class AggregatesSerializer : NServiceBus.Serialization.SerializationDefinition
    {
        public override Func<IMessageMapper, NServiceBus.Serialization.IMessageSerializer> Configure(ReadOnlySettings settings)
        {
            return mapper =>
            {
                var container = Configuration.Settings.Container;
                return new Serializer(container.Resolve<IMessageSerializer>());
            };
        }
    }
}
