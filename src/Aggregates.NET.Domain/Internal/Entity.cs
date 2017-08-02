﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Aggregates.Contracts;
using Aggregates.Exceptions;
using Aggregates.Extensions;
using NServiceBus;
using NServiceBus.Logging;
using NServiceBus.ObjectBuilder;

namespace Aggregates.Internal
{
    public abstract class Entity<TThis, TState> : IEventSource, IEventSourced, IHaveEntities<TThis>, INeedBuilder, INeedStream, INeedEventFactory, INeedRouteResolver where TThis : Entity<TThis, TState> where TState : class, IState, new()
    {
        internal static readonly ILog Logger = LogManager.GetLogger(typeof(TThis).Name);

        private IBuilder Builder => (this as INeedBuilder).Builder;
        
        private IMessageCreator EventFactory => (this as INeedEventFactory).EventFactory;

        private IRouteResolver Resolver => (this as INeedRouteResolver).Resolver;

        internal IEventStream Stream => (this as INeedStream).Stream;

        Id IEventSource.Id => Id;
        long IEventSource.Version => Version;
        IEventSource IEventSource.Parent => null;

        public Id Id => Stream.StreamId;
        public string Bucket => Stream.Bucket;
        public long Version => Stream.StreamVersion;
        public long CommitVersion => Stream.CommitVersion;

        private TState _state;

        public TState State
        {
            get
            {
                if (_state == null)
                    _state = new TState();
                return _state;
            }
        }


        IEventStream INeedStream.Stream { get; set; }
        IEventStream IEventSource.Stream => (this as INeedStream).Stream;

        IMessageCreator INeedEventFactory.EventFactory { get; set; }

        IRouteResolver INeedRouteResolver.Resolver { get; set; }
        IBuilder INeedBuilder.Builder { get; set; }


        public IRepository<TEntity, TThis> For<TEntity>() where TEntity : IEventSource
        {
            // Get current UOW
            var uow = Builder.Build<IUnitOfWork>();
            return uow.For<TEntity, TThis>(this as TThis);
        }
        public IPocoRepository<T, TThis> Poco<T>() where T : class, new()
        {
            // Get current UOW
            var uow = Builder.Build<IUnitOfWork>();
            return uow.Poco<T, TThis>(this as TThis);
        }

        public Task<long> GetSize(string oob)
        {
            var store = Builder.Build<IStoreStreams>();
            return store.GetSize<TThis>(Stream, oob);
        }

        public Task<IEnumerable<IFullEvent>> GetEvents(long start, int count, string oob = null)
        {
            var store = Builder.Build<IStoreStreams>();
            return store.GetEvents<TThis>(Stream, start, count, oob);
        }

        public Task<IEnumerable<IFullEvent>> GetEventsBackwards(long start, int count, string oob = null)
        {
            var store = Builder.Build<IStoreStreams>();
            return store.GetEventsBackwards<TThis>(Stream, start, count, oob);
        }
        

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        void IEventSourced.Hydrate(IEnumerable<IEvent> events)
        {
            Logger.Write(LogLevel.Debug, () => $"Hydrating {events.Count()} events to entity {GetType().FullName} stream [{Id}] bucket [{Bucket}]");
            foreach (var @event in events)
                RouteFor(@event);
        }
        void IEventSourced.Conflict(IEvent @event, IDictionary<string, string> metadata)
        {
            try
            {
                RouteForConflict(@event);
                RouteFor(@event);

                // Todo: Fill with user headers or something
                var headers = new Dictionary<string, string>();
                Stream.Add(@event, metadata);
            }
            catch (DiscardEventException) { }

        }

        void IEventSourced.Apply(IEvent @event, IDictionary<string, string> metadata)
        {
            Apply(@event, metadata);
        }
        void IEventSourced.Raise(IEvent @event, string id, IDictionary<string, string> metadata)
        {
            Raise(@event, id, metadata);
        }

        protected void DefineOob(string id, bool transient = false, int? daysToLive = null)
        {
            Stream.DefineOob(id, transient, daysToLive);
        }

        /// <summary>
        /// Apply an event to the current object's eventstream
        /// </summary>
        protected void Apply<TEvent>(Action<TEvent> action, IDictionary<string, string> metadata = null) where TEvent : IEvent
        {
            Logger.Write(LogLevel.Debug, () => $"Applying event {typeof(TEvent).FullName} to entity {GetType().FullName} stream [{Id}] bucket [{Bucket}]");
            var @event = EventFactory.CreateInstance(action);

            if (@event == null)
                throw new ArgumentException($"Failed to build event type {typeof(TEvent).FullName}");
            Apply(@event, metadata);
        }
        /// <summary>
        /// Publishes an event, but does not save to object's eventstream.  It will be stored under out of band event stream so as to not pollute entity's
        /// </summary>
        protected void Raise<TEvent>(Action<TEvent> action, string id, IDictionary<string, string> metadata = null) where TEvent : IEvent
        {
            Logger.Write(LogLevel.Debug, () => $"Raising an OOB event {typeof(TEvent).FullName} on entity {GetType().FullName} stream [{Id}] bucket [{Bucket}]");
            var @event = EventFactory.CreateInstance(action);

            if (@event == null)
                throw new ArgumentException($"Failed to build event type {typeof(TEvent).FullName}");
            Raise(@event, id, metadata);
        }

        private void Apply(IEvent @event, IDictionary<string, string> metadata = null)
        {
            RouteFor(@event);
            
            // Todo: Fill with user headers or something
            Stream.Add(@event, metadata);
        }
        private void Raise(IEvent @event, string id, IDictionary<string, string> metadata = null)
        {
            // Todo: Fill metadata with user headers or something
            Stream.AddOob(@event, id, metadata);
        }

        
        internal void RouteFor(IEvent @event)
        {
            var route = Resolver.Resolve(State, @event.GetType());
            if (route == null)
            {
                Logger.Write(LogLevel.Debug, () => $"Failed to route event {@event.GetType().FullName} on type {typeof(TThis).FullName}");
                return;
            }

            route(State, @event);
        }
        internal void RouteForConflict(IEvent @event)
        {
            var route = Resolver.Conflict(State, @event.GetType());
            if (route == null)
                throw new NoRouteException($"Failed to route {@event.GetType().FullName} for conflict resolution on entity {typeof(TThis).FullName} stream id [{Id}] bucket [{Bucket}].  If you want to handle conflicts here, define a new method of signature `private void Conflict({@event.GetType().Name} e)`");

            route(State, @event);
        }
    }
}
