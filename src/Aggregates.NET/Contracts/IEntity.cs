﻿using System;
using System.Collections.Generic;
using System.Text;
using Aggregates.Messages;

namespace Aggregates.Contracts
{
    public interface IEntity
    {
        Id Id { get; }
        string Bucket { get; }
        IEnumerable<Id> Parents { get; }

        long Version { get; }
        bool Dirty { get; }
        IFullEvent[] Uncommitted { get; }
    }

    public interface IEntity<out TState> : IEntity where TState : IState, new()
    {
        TState State { get; }

        void Conflict(IEvent @event);
        void Apply(IEvent @event);
        void Raise(IEvent @event, string id, bool transient = false, int? daysToLive = null);
    }
    public interface IChildEntity<out TParent> where TParent : IEntity
    {
        TParent Parent { get; }
    }
}
