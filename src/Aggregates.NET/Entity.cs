﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aggregates.Contracts;
using Aggregates.DI;
using Aggregates.Internal;
using Aggregates.Logging;
using Aggregates.Messages;

namespace Aggregates
{
    public abstract class Entity<TThis, TParent, TState> : Entity<TThis, TState>, IChildEntity<TParent> where TParent : Entity<TParent, TState> where TThis : Entity<TThis, TParent, TState> where TState : IState, new()
    {
        TParent IChildEntity<TParent>.Parent => Parent;

        public TParent Parent { get; internal set; }
    }

    public abstract class Entity<TThis, TState> : IEntity<TState>, IHaveEntities<TThis>, INeedContainer where TThis : Entity<TThis, TState> where TState : IState, new()
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(TThis).Name);

        public Id Id { get; internal set; }
        public string Bucket { get; internal set; }
        public IEnumerable<Id> Parents { get; internal set; }
        public long Version { get; internal set; }

        public bool Dirty => Uncommitted.Any();

        public IFullEvent[] Uncommitted => _uncommitted.ToArray();

        public TState State { get; internal set; }
        
        private readonly IList<IFullEvent> _uncommitted = new List<IFullEvent>();

        private TinyIoCContainer Container => (this as INeedContainer).Container;
        TinyIoCContainer INeedContainer.Container { get; set; }


        public IRepository<TThis, TEntity> For<TEntity>() where TEntity : IChildEntity<TThis>
        {
            // Get current UOW
            var uow = Container.Resolve<IDomainUnitOfWork>();
            return uow.For<TThis, TEntity>(this as TThis);
        }
        public IPocoRepository<TThis, T> Poco<T>() where T : class, new()
        {
            // Get current UOW
            var uow = Container.Resolve<IDomainUnitOfWork>();
            return uow.Poco<TThis, T>(this as TThis);
        }
        public Task<long> GetSize(string oob = null)
        {
            var store = Container.Resolve<IStoreEvents>();

            var bucket = Bucket;
            if (!string.IsNullOrEmpty(oob))
                bucket = $"OOB-{oob}";

            return store.Size<TThis>(bucket, Id, Parents);
        }

        public Task<IFullEvent[]> GetEvents(long start, int count, string oob = null)
        {
            var store = Container.Resolve<IStoreEvents>();

            var bucket = Bucket;
            if (!string.IsNullOrEmpty(oob))
                bucket = $"OOB-{oob}";

            return store.GetEvents<TThis>(bucket, Id, Parents, start, count);
        }

        public Task<IFullEvent[]> GetEventsBackwards(long start, int count, string oob = null)
        {
            var store = Container.Resolve<IStoreEvents>();

            var bucket = Bucket;
            if (!string.IsNullOrEmpty(oob))
                bucket = $"OOB-{oob}";

            return store.GetEventsBackwards<TThis>(bucket, Id, Parents, start, count);
        }

        void IEntity<TState>.Conflict(IEvent @event)
        {
            // if conflict handling fails it throws exception
            State.Conflict(@event);
            Apply(@event);
        }

        void IEntity<TState>.Apply(IEvent @event)
        {
            Apply(@event);
        }

        void IEntity<TState>.Raise(IEvent @event, string id, bool transient, int? daysToLive)
        {
            Raise(@event, id, transient, daysToLive);
        }

        protected void Apply(IEvent @event)
        {
            State.Apply(@event);
            _uncommitted.Add(new FullEvent
            {
                Descriptor = new EventDescriptor
                {
                    EntityType = typeof(TThis).AssemblyQualifiedName,
                    StreamType = StreamTypes.Domain,
                    Bucket = Bucket,
                    StreamId = Id,
                    Parents = Parents,
                    Timestamp = DateTime.UtcNow,
                    Version = State.Version,
                    Headers = new Dictionary<string, string>()
                },
                Event = @event
            });
        }

        protected void Raise(IEvent @event, string id, bool transient = false, int? daysToLive = null)
        {
            _uncommitted.Add(new FullEvent
            {
                Descriptor = new EventDescriptor
                {
                    EntityType = typeof(TThis).AssemblyQualifiedName,
                    StreamType = StreamTypes.OOB,
                    Bucket = Bucket,
                    StreamId = Id,
                    Parents = Parents,
                    Timestamp = DateTime.UtcNow,
                    Version = State.Version,
                    Headers = new Dictionary<string, string>()
                    {
                        { Defaults.OobHeaderKey, id },
                        { Defaults.OobTransientKey, transient.ToString() },
                        { Defaults.OobDaysToLiveKey, daysToLive.ToString() }
                    }
                },
                Event = @event
            });
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}
