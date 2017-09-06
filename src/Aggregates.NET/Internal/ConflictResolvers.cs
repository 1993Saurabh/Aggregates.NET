﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aggregates.Contracts;
using Aggregates.DI;
using Aggregates.Exceptions;
using Aggregates.Extensions;
using Aggregates.Logging;
using Aggregates.Messages;

namespace Aggregates.Internal
{
    delegate IResolveConflicts ResolverBuilder(TinyIoCContainer container, Type type);

    class ConcurrencyStrategy : Enumeration<ConcurrencyStrategy, ConcurrencyConflict>
    {
        public static ConcurrencyStrategy Throw = new ConcurrencyStrategy(ConcurrencyConflict.Throw, "Throw", (b, _) => b.Resolve<ThrowConflictResolver>());

        public static ConcurrencyStrategy Ignore = new ConcurrencyStrategy(ConcurrencyConflict.Ignore, "Ignore",
            (b, _) => new IgnoreConflictResolver(b.Resolve<IStoreEvents>(), b.Resolve<StreamIdGenerator>("StreamGenerator")));

        public static ConcurrencyStrategy Discard = new ConcurrencyStrategy(ConcurrencyConflict.Discard, "Discard", (b, _) => b.Resolve<DiscardConflictResolver>());

        public static ConcurrencyStrategy ResolveStrongly = new ConcurrencyStrategy(ConcurrencyConflict.ResolveStrongly, "ResolveStrongly",
            (b, _) => new ResolveStronglyConflictResolver(b.Resolve<IStoreSnapshots>(), b.Resolve<IStoreEvents>(), b.Resolve<StreamIdGenerator>("StreamGenerator")));

        public static ConcurrencyStrategy ResolveWeakly = new ConcurrencyStrategy(ConcurrencyConflict.ResolveWeakly, "ResolveWeakly",
            (b, _) => new ResolveWeaklyConflictResolver(b.Resolve<IStoreSnapshots>(), b.Resolve<IStoreEvents>(), b.Resolve<IDelayedChannel>(), b.Resolve<StreamIdGenerator>("StreamGenerator")));

        public static ConcurrencyStrategy Custom = new ConcurrencyStrategy(ConcurrencyConflict.Custom, "Custom", (b, type) => (IResolveConflicts)b.Resolve(type));

        public ConcurrencyStrategy(ConcurrencyConflict value, string displayName, ResolverBuilder builder) : base(value, displayName)
        {
            Build = builder;
        }

        public ResolverBuilder Build { get; private set; }
    }

    internal class ThrowConflictResolver : IResolveConflicts
    {
        public Task Resolve<TEntity, TState>(TEntity entity, IFullEvent[] uncommitted, Guid commitId, IDictionary<string, string> commitHeaders) where TEntity : IEntity<TState> where TState : IState, new()
        {
            throw new ConflictResolutionFailedException("No conflict resolution attempted");
        }
    }

    /// <summary>
    /// Conflict from the store is ignored, events will always be written
    /// </summary>
    internal class IgnoreConflictResolver : IResolveConflicts
    {
        internal static readonly ILog Logger = LogProvider.GetLogger("IgnoreConflictResolver");

        private readonly IStoreEvents _store;
        private readonly StreamIdGenerator _streamGen;

        public IgnoreConflictResolver(IStoreEvents store, StreamIdGenerator streamGen)
        {
            _store = store;
            _streamGen = streamGen;
        }

        public async Task Resolve<TEntity, TState>(TEntity entity, IFullEvent[] uncommitted, Guid commitId, IDictionary<string, string> commitHeaders) where TEntity : IEntity<TState> where TState : IState, new()
        {
            var state = entity.State;
            
            Logger.Write(LogLevel.Info, () => $"Resolving {uncommitted.Count()} uncommitted events to stream [{entity.Id}] type [{typeof(TEntity).FullName}] bucket [{entity.Bucket}]");

            foreach (var u in uncommitted)
            {
                state.Apply(u.Event as IEvent);
            }
            
            await _store.WriteEvents<TEntity>(entity.Bucket, entity.Id, entity.Parents, uncommitted, commitHeaders).ConfigureAwait(false);
        }
    }
    /// <summary>
    /// Conflicted events are discarded
    /// </summary>
    internal class DiscardConflictResolver : IResolveConflicts
    {
        internal static readonly ILog Logger = LogProvider.GetLogger("DiscardConflictResolver");

        public Task Resolve<TEntity, TState>(TEntity entity, IFullEvent[] uncommitted, Guid commitId, IDictionary<string, string> commitHeaders) where TEntity : IEntity<TState> where TState : IState, new()
        {
            Logger.Write(LogLevel.Info, () => $"Discarding {uncommitted.Count()} conflicting uncommitted events to stream [{entity.Id}] type [{typeof(TEntity).FullName}] bucket [{entity.Bucket}]");

            return Task.CompletedTask;
        }
    }
    /// <summary>
    /// Pull latest events from store, merge into stream and re-commit
    /// </summary>
    internal class ResolveStronglyConflictResolver : IResolveConflicts
    {
        internal static readonly ILog Logger = LogProvider.GetLogger("ResolveStronglyConflictResolver");

        private readonly IStoreSnapshots _snapstore;
        private readonly IStoreEvents _eventstore;
        private readonly StreamIdGenerator _streamGen;

        public ResolveStronglyConflictResolver(IStoreSnapshots snapshot, IStoreEvents eventstore, StreamIdGenerator streamGen)
        {
            _snapstore = snapshot;
            _eventstore = eventstore;
            _streamGen = streamGen;
        }

        public async Task Resolve<TEntity, TState>(TEntity entity, IFullEvent[] uncommitted, Guid commitId, IDictionary<string, string> commitHeaders) where TEntity : IEntity<TState> where TState : IState, new()
        {
            var state = entity.State;
            Logger.Write(LogLevel.Info, () => $"Resolving {uncommitted.Count()} uncommitted events to stream [{entity.Id}] type [{typeof(TEntity).FullName}] bucket [{entity.Bucket}]");

            var latestEvents =
                await _eventstore.GetEvents<TEntity>(entity.Bucket, entity.Id, entity.Parents, entity.Version).ConfigureAwait(false);
            Logger.Write(LogLevel.Info, () => $"Stream is {latestEvents.Count()} events behind store");

            for (var i = 0; i < latestEvents.Length; i++)
                state.Apply(latestEvents[i].Event as IEvent);
            
            Logger.Write(LogLevel.Debug, () => "Merging conflicted events");
            try
            {
                foreach (var u in uncommitted)
                {
                    if (u.Descriptor.StreamType == StreamTypes.Domain)
                        entity.Conflict(u.Event as IEvent);
                    else if (u.Descriptor.StreamType == StreamTypes.OOB)
                        entity.Raise(u.Event as IEvent, u.Descriptor.Headers[Defaults.OobHeaderKey]);
                }
            }
            catch (NoRouteException e)
            {
                Logger.Write(LogLevel.Info, () => $"Failed to resolve conflict: {e.Message}");
                throw new ConflictResolutionFailedException("Failed to resolve conflict", e);
            }

            Logger.Write(LogLevel.Debug, () => "Successfully merged conflicted events");


            await _eventstore.WriteEvents<TEntity>(entity.Bucket, entity.Id, entity.Parents, entity.Uncommitted, commitHeaders).ConfigureAwait(false);

            if (state.ShouldSnapshot())
            {
                Logger.Write(LogLevel.Debug,
                    () => $"Taking snapshot of {typeof(TEntity).FullName} id [{entity.Id}] version {entity.Version}");
                await _snapstore.WriteSnapshots<TEntity>(entity.Bucket, entity.Id, entity.Parents, state.Version, state, commitHeaders)
                    .ConfigureAwait(false);
            }
        }
    }

    internal class ConflictingEvents : IMessage
    {
        public string EntityType { get; set; }
        public string Bucket { get; set; }
        public Id StreamId { get; set; }
        public IEnumerable<Tuple<string, Id>> Parents { get; set; }

        public IEnumerable<IFullEvent> Events { get; set; }
    }
    /// <summary>
    /// Save conflicts for later processing, can only be used if the stream can never fail to merge
    /// </summary>
    internal class ResolveWeaklyConflictResolver :
        IResolveConflicts
    {
        internal static readonly ILog Logger = LogProvider.GetLogger("ResolveWeaklyConflictResolver");

        private readonly IStoreSnapshots _snapstore;
        private readonly IStoreEvents _eventstore;
        private readonly IDelayedChannel _delay;
        private readonly StreamIdGenerator _streamGen;


        public ResolveWeaklyConflictResolver(IStoreSnapshots snapstore, IStoreEvents eventstore, IDelayedChannel delay, StreamIdGenerator streamGen)
        {
            _snapstore = snapstore;
            _eventstore = eventstore;
            _delay = delay;
            _streamGen = streamGen;
        }


        public Task Resolve<TEntity, TState>(TEntity entity, IFullEvent[] uncommitted, Guid commitId, IDictionary<string, string> commitHeaders) where TEntity : IEntity<TState> where TState : IState, new()
        {
            throw new NotImplementedException();
        }
    }
    


    
}
