﻿using Aggregates.Contracts;
using Aggregates.DI;
using Aggregates.Exceptions;
using Aggregates.Extensions;
using Aggregates.Internal;
using Aggregates.Messages;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Aggregates
{
    public class Configure
    {
        // Log settings
        public TimeSpan? SlowAlertThreshold { get; private set; }
        public bool ExtraStats { get; private set; }

        // Data settings
        public StreamIdGenerator Generator { get; private set; }
        public int ReadSize { get; private set; }
        public Compression Compression { get; private set; }

        // Messaging settings
        public string UniqueAddress { get; private set; }
        public int Retries { get; private set; }
        public int ParallelMessages { get; private set; }
        public int ParallelEvents { get; private set; }
        public int MaxConflictResolves { get; private set; }

        // Delayed cache settings
        public int FlushSize { get; private set; }
        public TimeSpan FlushInterval { get; private set; }
        public TimeSpan DelayedExpiration { get; private set; }
        public int MaxDelayed { get; private set; }

        internal List<Func<Task>> StartupTasks;
        internal List<Func<Task>> ShutdownTasks;

        public Configure()
        {
            StartupTasks = new List<Func<Task>>();
            ShutdownTasks = new List<Func<Task>>();

            // Set sane defaults
            Generator = new StreamIdGenerator((type, streamType, bucket, stream, parents) => $"{streamType}-{bucket}-[{parents.BuildParentsString()}]-{type.FullName.Replace(".", "")}-{stream}");
            ReadSize = 100;
            Compression = Compression.None;
            UniqueAddress = Guid.NewGuid().ToString("N");
            Retries = 10;
            ParallelMessages = 10;
            ParallelEvents = 10;
            MaxConflictResolves = 3;
            FlushSize = 500;
            FlushInterval = TimeSpan.FromMinutes(1);
            DelayedExpiration = TimeSpan.FromMinutes(5);
            MaxDelayed = 5000;

            var container = TinyIoCContainer.Current;

            container.Register<IRepositoryFactory, RepositoryFactory>().AsMultiInstance();
            container.Register<IProcessor, Processor>().AsMultiInstance();
            container.Register<IMetrics, NullMetrics>().AsMultiInstance();
            container.Register<IDelayedChannel, DelayedChannel>().AsMultiInstance();
            container.Register<IDomainUnitOfWork, UnitOfWork>().AsMultiInstance();

            container.Register<IDelayedCache, DelayedCache>().AsSingleton();
            container.Register<ICache, IntelligentCache>().AsSingleton();
            container.Register<ISnapshotReader, SnapshotReader>().AsSingleton();

            container.Register<IEventSubscriber>((factory, overloads) => new EventSubscriber(factory.Resolve<IMetrics>(), factory.Resolve<IMessaging>(), factory.Resolve<IEventStoreConsumer>(), ParallelEvents), "eventsubscriber").AsSingleton();
            container.Register<IEventSubscriber>((factory, overloads) => new DelayedSubscriber(factory.Resolve<IMetrics>(), factory.Resolve<IEventStoreConsumer>(), factory.Resolve<IMessageDispatcher>(), Retries), "delayedsubscriber").AsSingleton();
            container.Register<IEventSubscriber>((factory, overloads) => (IEventSubscriber)factory.Resolve<ISnapshotReader>(), "snapshotreader").AsSingleton();

            container.Register<Func<Exception, string, Error>>((factory, overloads) =>
            {
                var eventFactory = factory.Resolve<IEventFactory>();
                return (exception, message) =>
                {
                    var sb = new StringBuilder();
                    if (!string.IsNullOrEmpty(message))
                    {
                        sb.AppendLine($"Error Message: {message}");
                    }
                    sb.AppendLine($"Exception type {exception.GetType()}");
                    sb.AppendLine($"Exception message: {exception.Message}");
                    sb.AppendLine($"Stack trace: {exception.StackTrace}");


                    if (exception.InnerException != null)
                    {
                        sb.AppendLine("---BEGIN Inner Exception--- ");
                        sb.AppendLine($"Exception type {exception.InnerException.GetType()}");
                        sb.AppendLine($"Exception message: {exception.InnerException.Message}");
                        sb.AppendLine($"Stack trace: {exception.InnerException.StackTrace}");
                        sb.AppendLine("---END Inner Exception---");

                    }
                    var aggregateException = exception as System.AggregateException;
                    if (aggregateException == null)
                        return eventFactory.Create<Error>(e => { e.Message = sb.ToString(); });

                    sb.AppendLine("---BEGIN Aggregate Exception---");
                    var aggException = aggregateException;
                    foreach (var inner in aggException.InnerExceptions)
                    {

                        sb.AppendLine("---BEGIN Inner Exception--- ");
                        sb.AppendLine($"Exception type {inner.GetType()}");
                        sb.AppendLine($"Exception message: {inner.Message}");
                        sb.AppendLine($"Stack trace: {inner.StackTrace}");
                        sb.AppendLine("---END Inner Exception---");
                    }

                    return eventFactory.Create<Error>(e =>
                    {
                        e.Message = sb.ToString();
                    });
                };
            });

            container.Register<Func<Accept>>((factory, overloads) =>
            {
                var eventFactory = factory.Resolve<IEventFactory>();
                return () => eventFactory.Create<Accept>(x => { });
            });

            container.Register<Func<string, Reject>>((factory, overloads) =>
            {
                var eventFactory = factory.Resolve<IEventFactory>();
                return message => { return eventFactory.Create<Reject>(e => { e.Message = message; }); };
            });
            container.Register<Func<BusinessException, Reject>>((factory, overloads) =>
            {
                var eventFactory = factory.Resolve<IEventFactory>();
                return exception => {
                    return eventFactory.Create<Reject>(e => {
                        e.Message = "Exception raised";
                    });
                };
            });

            StartupTasks.Add(async () =>
            {
                var subscribers = TinyIoCContainer.Current.ResolveAll<IEventSubscriber>();

                await subscribers.WhenAllAsync(x => x.Setup(
                    UniqueAddress,
                    Assembly.GetEntryAssembly().GetName().Version)
                ).ConfigureAwait(false);

                await subscribers.WhenAllAsync(x => x.Connect()).ConfigureAwait(false);

            });
            ShutdownTasks.Add(async () =>
            {
                var subscribers = TinyIoCContainer.Current.ResolveAll<IEventSubscriber>();

                await subscribers.WhenAllAsync(x => x.Shutdown()).ConfigureAwait(false);
            });
        }

        public Configure SetSlowAlertThreshold(TimeSpan? threshold)
        {
            SlowAlertThreshold = threshold;
            return this;
        }
        public Configure SetExtraStats(bool extra)
        {
            ExtraStats = extra;
            return this;
        }
        public Configure SetStreamIdGenerator(StreamIdGenerator generator)
        {
            Generator = generator;
            return this;
        }
        public Configure SetReadSize(int readsize)
        {
            ReadSize = readsize;
            return this;
        }
        public Configure SetCompression(Compression compression)
        {
            Compression = compression;
            return this;
        }
        public Configure SetUniqueAddress(string address)
        {
            UniqueAddress = address;
            return this;
        }
        public Configure SetRetries(int retries)
        {
            Retries = retries;
            return this;
        }
        public Configure SetParallelMessages(int parallel)
        {
            ParallelMessages = parallel;
            return this;
        }
        public Configure SetParallelEvents(int parallel)
        {
            ParallelEvents = parallel;
            return this;
        }
        public Configure SetMaxConflictResolves(int attempts)
        {
            MaxConflictResolves = attempts;
            return this;
        }
        public Configure SetFlushSize(int size)
        {
            FlushSize = size;
            return this;
        }
        public Configure SetFlushInterval(TimeSpan interval)
        {
            FlushInterval = interval;
            return this;
        }
        public Configure SetDelayedExpiration(TimeSpan expiration)
        {
            DelayedExpiration = expiration;
            return this;
        }
        public Configure SetMaxDelayed(int max)
        {
            MaxDelayed = max;
            return this;
        }

    }
}
