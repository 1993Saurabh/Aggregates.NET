﻿using Aggregates.Contracts;
using Aggregates.DI;
using App.Metrics;
using NServiceBus;
using NServiceBus.Extensibility;
using NServiceBus.Pipeline;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Aggregates.UnitTests.NServiceBus
{
    [TestFixture]
    public class UnitOfWorkExecutor
    {

        private Moq.Mock<IMetrics> _metrics;
        private Moq.Mock<IIncomingLogicalMessageContext> _context;
        private Moq.Mock<IDomainUnitOfWork> _domainUow;
        private Moq.Mock<IUnitOfWork> _uow;
        private Moq.Mock<Func<Task>> _next;

        private ContextBag _contextBag;
        private Aggregates.Internal.UnitOfWorkExecutor _executor;

        [SetUp]
        public void Setup()
        {
            _metrics = new Moq.Mock<IMetrics>();
            _context = new Moq.Mock<IIncomingLogicalMessageContext>();
            _domainUow = new Moq.Mock<IDomainUnitOfWork>();
            _uow = new Moq.Mock<IUnitOfWork>();
            _next = new Moq.Mock<Func<Task>>();
            _contextBag = new ContextBag();

            TinyIoCContainer.Current.Register(_domainUow.Object);
            TinyIoCContainer.Current.Register(_uow.Object);

            _metrics.Setup(x => x.Measure.Meter.Mark(Moq.It.IsAny<App.Metrics.Core.Options.MeterOptions>()));
            _metrics.Setup(x => x.Measure.Counter.Increment(Moq.It.IsAny<App.Metrics.Core.Options.CounterOptions>()));
            _metrics.Setup(x => x.Measure.Counter.Decrement(Moq.It.IsAny<App.Metrics.Core.Options.CounterOptions>()));
            _metrics.Setup(x => x.Measure.Timer.Time(Moq.It.IsAny<App.Metrics.Core.Options.TimerOptions>()));

            _context.Setup(x => x.Extensions).Returns(_contextBag);
            _context.Setup(x => x.MessageHeaders).Returns(new Dictionary<string, string>
            {
                [Headers.MessageIntent] = MessageIntentEnum.Send.ToString()
            });
            _context.Setup(x => x.Message).Returns(new LogicalMessage(new global::NServiceBus.Unicast.Messages.MessageMetadata(typeof(int)), 1));

            _executor = new Internal.UnitOfWorkExecutor(_metrics.Object);
        }

        [Test]
        public async Task normal()
        {
            await _executor.Invoke(_context.Object, _next.Object);

            _domainUow.Verify(x => x.Begin(), Moq.Times.Once);
            _uow.Verify(x => x.Begin(), Moq.Times.Once);
            _domainUow.Verify(x => x.End(Moq.It.IsAny<Exception>()), Moq.Times.Once);
            _uow.Verify(x => x.End(Moq.It.IsAny<Exception>()), Moq.Times.Once);
            _next.Verify(x => x(), Moq.Times.Once);
        }

        [Test]
        public void throw_exception()
        {
            _next.Setup(x => x()).Throws(new Exception());

            Assert.ThrowsAsync<Exception>(() => _executor.Invoke(_context.Object, _next.Object));

            _domainUow.Verify(x => x.Begin(), Moq.Times.Once);
            _uow.Verify(x => x.Begin(), Moq.Times.Once);

            _domainUow.Verify(x => x.End(Moq.It.IsNotNull<Exception>()), Moq.Times.Once);
            _uow.Verify(x => x.End(Moq.It.IsNotNull<Exception>()), Moq.Times.Once);
            _next.Verify(x => x(), Moq.Times.Once);
        }

        [Test]
        public void uow_end_throws_too()
        {
            _next.Setup(x => x()).Throws<Exception>();
            _uow.Setup(x => x.End(Moq.It.IsAny<Exception>())).Throws<Exception>();

            // Should produce AggregateException not Exception
            Assert.ThrowsAsync<AggregateException>(() => _executor.Invoke(_context.Object, _next.Object));

            _domainUow.Verify(x => x.Begin(), Moq.Times.Once);
            _uow.Verify(x => x.Begin(), Moq.Times.Once);

            _domainUow.Verify(x => x.End(Moq.It.IsNotNull<Exception>()), Moq.Times.Once);
            _uow.Verify(x => x.End(Moq.It.IsNotNull<Exception>()), Moq.Times.Once);
            _next.Verify(x => x(), Moq.Times.Once);
        }

        [Test]
        public async Task event_delivered()
        {
            _contextBag.Set(Defaults.EventHeader, new object());

            await _executor.Invoke(_context.Object, _next.Object);

            _context.Verify(x => x.UpdateMessageInstance(Moq.It.IsAny<object>()), Moq.Times.Once);
            _domainUow.Verify(x => x.Begin(), Moq.Times.Once);
            _uow.Verify(x => x.Begin(), Moq.Times.Once);
            _domainUow.Verify(x => x.End(Moq.It.IsAny<Exception>()), Moq.Times.Once);
            _uow.Verify(x => x.End(Moq.It.IsAny<Exception>()), Moq.Times.Once);
            _next.Verify(x => x(), Moq.Times.Once);
        }

        [Test]
        public async Task bulk_event_delivered()
        {
            var delayed = new Moq.Mock<IDelayedMessage>();

            delayed.Setup(x => x.Headers).Returns(new Dictionary<string, string>
            {
                ["test"] = "test"
            });

            var events = new IDelayedMessage[] { delayed.Object };

            _contextBag.Set(Defaults.LocalBulkHeader, events);

            var headers = new Dictionary<string, string>();
            _context.Setup(x => x.Headers).Returns(headers);

            await _executor.Invoke(_context.Object, _next.Object);

            Assert.IsTrue(headers.ContainsKey($"{Defaults.DelayedPrefixHeader}.test"));
            Assert.AreEqual("test", headers[$"{Defaults.DelayedPrefixHeader}.test"]);

            _context.Verify(x => x.UpdateMessageInstance(Moq.It.IsAny<object>()), Moq.Times.Once);
            _domainUow.Verify(x => x.Begin(), Moq.Times.Once);
            _uow.Verify(x => x.Begin(), Moq.Times.Once);
            _domainUow.Verify(x => x.End(Moq.It.IsAny<Exception>()), Moq.Times.Once);
            _uow.Verify(x => x.End(Moq.It.IsAny<Exception>()), Moq.Times.Once);
            _next.Verify(x => x(), Moq.Times.Once);
        }

        [Test]
        public async Task multiple_bulk_events()
        {
            var delayed = new Moq.Mock<IDelayedMessage>();
            delayed.Setup(x => x.Headers).Returns(new Dictionary<string, string>
            {
                ["test"] = "test"
            });

            var events = new IDelayedMessage[] { delayed.Object, delayed.Object, delayed.Object };

            _contextBag.Set(Defaults.LocalBulkHeader, events);

            var headers = new Dictionary<string, string>();
            _context.Setup(x => x.Headers).Returns(headers);

            await _executor.Invoke(_context.Object, _next.Object);

            Assert.IsTrue(headers.ContainsKey($"{Defaults.DelayedPrefixHeader}.test"));
            Assert.AreEqual("test", headers[$"{Defaults.DelayedPrefixHeader}.test"]);

            _context.Verify(x => x.UpdateMessageInstance(Moq.It.IsAny<object>()), Moq.Times.Exactly(3));
            _domainUow.Verify(x => x.Begin(), Moq.Times.Once);
            _uow.Verify(x => x.Begin(), Moq.Times.Once);
            _domainUow.Verify(x => x.End(Moq.It.IsAny<Exception>()), Moq.Times.Once);
            _uow.Verify(x => x.End(Moq.It.IsAny<Exception>()), Moq.Times.Once);
            _next.Verify(x => x(), Moq.Times.Exactly(3));
        }
    }
}
