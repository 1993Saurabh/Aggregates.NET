﻿using Aggregates.Contracts;
using Aggregates.Logging;
using Aggregates.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Aggregates.Internal
{

    class DelayedCache : IDelayedCache, IDisposable
    {
        class CacheKey
        {
            public CacheKey(string channel, string key)
            {
                Channel = channel;
                Key = key;
            }
            public string Channel { get; set; }
            public string Key { get; set; }

            public class EqualityComparer : IEqualityComparer<CacheKey>
            {

                public bool Equals(CacheKey x, CacheKey y)
                {
                    return x.Channel == y.Channel && x.Key == y.Key;
                }

                public int GetHashCode(CacheKey x)
                {
                    return (x?.Channel?.GetHashCode() ?? 1) ^ (x?.Key?.GetHashCode() ?? 1);
                }

            }
        }
        class CachedList
        {
            private object _lock { get; set; }
            private Queue<IDelayedMessage> _messages;

            public long Created { get; private set; }
            public long Pulled { get; private set; }

            public int Count => _messages.Count;

            public CachedList()
            {
                _lock = new object();
                _messages = new Queue<IDelayedMessage>();
                Created = DateTime.UtcNow.Ticks;
                Pulled = DateTime.UtcNow.Ticks;
            }

            public void AddRange(IDelayedMessage[] messages)
            {
                lock (_lock)
                {
                    foreach (var m in messages)
                        _messages.Enqueue(m);
                }
            }
            public IDelayedMessage[] Dequeue(int? count)
            {
                Pulled = DateTime.UtcNow.Ticks;

                count = count ?? int.MaxValue;

                var discovered = new List<IDelayedMessage>();
                lock (_lock)
                {
                    try
                    {
                        while (count > 0)
                        {
                            discovered.Add(_messages.Dequeue());
                            count--;
                        }
                    }
                    catch (InvalidOperationException) { }
                }

                return discovered.ToArray();
            }
        }

        private static readonly ILog Logger = LogProvider.GetLogger("DelayedCache");


        private readonly IMetrics _metrics;
        private readonly IStoreEvents _store;
        private readonly TimeSpan _flushInterval;
        private readonly string _endpoint;
        private readonly int _maxSize;
        private readonly int _flushSize;
        private readonly TimeSpan _expiration;
        private readonly StreamIdGenerator _streamGen;
        private readonly Thread _thread;
        private readonly CancellationTokenSource _cts;

        private readonly object _cacheLock;
        private readonly Dictionary<CacheKey, CachedList> _memCache;
        private readonly Random _rand;

        private int _tooLarge;
        private bool _disposed;

        public DelayedCache(IMetrics metrics, IStoreEvents store, TimeSpan flushInterval, string endpoint, int maxSize, int flushSize, TimeSpan delayedExpiration, StreamIdGenerator streamGen)
        {
            _metrics = metrics;
            _store = store;
            _flushInterval = flushInterval;
            _endpoint = endpoint;
            _maxSize = maxSize;
            _flushSize = flushSize;
            _expiration = delayedExpiration;
            _streamGen = streamGen;
            _cts = new CancellationTokenSource();

            _cacheLock = new object();
            _memCache = new Dictionary<CacheKey, CachedList>(new CacheKey.EqualityComparer());
            _rand = new Random();

            _thread = new Thread(Threaded)
            { IsBackground = true, Name = $"Delayed Cache Thread" };

            // Add a process exit event handler to flush cached delayed events before exiting the app
            // Not perfect in the case of a fatal app error - but something
            AppDomain.CurrentDomain.ProcessExit += (sender, e) => Threaded(this);
            _thread.Start(this);
        }

        private static void Threaded(object state)
        {
            var cache = (DelayedCache)state;

            try
            {
                while (true)
                {
                    cache._cts.Token.ThrowIfCancellationRequested();

                    Task.Delay(cache._flushInterval, cache._cts.Token).Wait();
                    cache.Flush().Wait(cache._cts.Token);
                }
            }
            catch { }
        }


        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cts.Cancel();
            _thread.Join();
        }

        public async Task Add(string channel, string key, IDelayedMessage[] messages)
        {
            // If cache grows larger than 150% of max cache size, pause all processing until flush finished
            while (Interlocked.CompareExchange(ref _tooLarge, 1, 1) == 1)
                await Task.Delay(50).ConfigureAwait(false);

            // Anything without a key bypasses memory cache
            if (string.IsNullOrEmpty(key))
            {
                var translatedEvents = messages.Select(x => (IFullEvent)new FullEvent
                {
                    Descriptor = new EventDescriptor
                    {
                        EntityType = "DELAY",
                        StreamType = $"{_endpoint}.{StreamTypes.Delayed}",
                        Bucket = Assembly.GetEntryAssembly()?.FullName ?? "UNKNOWN",
                        StreamId = channel,
                        Timestamp = DateTime.UtcNow,
                        Headers = new Dictionary<string, string>()
                        {
                            ["Expired"] = "true",
                            ["FlushTime"] = DateTime.UtcNow.ToString("s"),
                            ["Instance"] = Defaults.Instance.ToString(),
                            ["Machine"] = Environment.MachineName,
                        }
                    },
                    Event = x,
                }).ToArray();
                try
                {
                    var streamName = _streamGen(typeof(DelayedCache),
                        $"{_endpoint}.{StreamTypes.Delayed}",
                        Assembly.GetEntryAssembly()?.FullName ?? "UNKNOWN", channel, new Id[] { });
                    await _store.WriteEvents(streamName, translatedEvents, null).ConfigureAwait(false);
                    return;
                }
                catch (Exception e)
                {
                    Logger.WarnEvent("WriteFailure", e, "Write to [{Channel}] failed: {ExceptionType} - {ExceptionMessage}", channel, e.GetType().Name, e.Message);
                }
            }

            addToMemCache(channel, key, messages);
        }

        private void addToMemCache(string channel, string key, IDelayedMessage[] messages)
        {

            var cacheKey = new CacheKey(channel, key);
            CachedList list;
            lock (_cacheLock)
            {
                if (!_memCache.TryGetValue(cacheKey, out list))
                    _memCache[cacheKey] = list = new CachedList();
            }

            list.AddRange(messages);
        }

        public Task<IDelayedMessage[]> Pull(string channel, string key = null, int? max = null)
        {
            var discovered = pullFromMemCache(channel, key, max);
            
            // Check empty key store too, 10% of the time
            if (_rand.Next(10) == 0 && !string.IsNullOrEmpty(key))
            {
                var nonSpecific = pullFromMemCache(channel, null, max);
                discovered = discovered.Concat(nonSpecific).ToArray();
            }
            
            Logger.DebugEvent("Pull", "{Messages} from channel [{Channel}] key [{Key}]", discovered.Length, channel, key);
            return Task.FromResult(discovered);
        }

        public Task<TimeSpan?> Age(string channel, string key)
        {

            var specificAge = TimeSpan.Zero;

            // Get age from memcache
            var specificKey = new CacheKey(channel, key);
            CachedList temp;
            if (_memCache.TryGetValue(specificKey, out temp))
                specificAge = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - temp.Pulled);
            
            Logger.DebugEvent("Age", "{Age} ms channel [{Channel}] key [{Key}]", specificAge.TotalMilliseconds, channel, key);

            return Task.FromResult<TimeSpan?>(specificAge);
        }

        public Task<int> Size(string channel, string key)
        {
            var specificSize = 0;
            // Get size from memcache
            var specificKey = new CacheKey(channel, key);
            CachedList temp;
            if (_memCache.TryGetValue(specificKey, out temp))
                specificSize = temp.Count;

            Logger.DebugEvent("Size", "{Size} channel [{Channel}] key [{Key}]", specificSize, channel, key);
            return Task.FromResult(specificSize);
        }

        private IDelayedMessage[] pullFromMemCache(string channel, string key = null, int? max = null)
        {
            var cacheKey = new CacheKey(channel, key);

            // Check memcache even if key == null because messages failing to save to ES are put in memcache
            var messages = new IDelayedMessage[] { };
            lock (_cacheLock)
            {
                if (_memCache.TryGetValue(cacheKey, out var fromCache))
                    _memCache.Remove(cacheKey);

                if (fromCache == null)
                    return messages;

                messages = fromCache.Dequeue(max);
                if (fromCache.Count != 0)
                    _memCache[cacheKey] = fromCache;
            }
            return messages;
        }

        private async Task Flush()
        {
            var memCacheTotalSize = _memCache.Values.Sum(x => x.Count);
            _metrics.Update("Delayed Cache Size", Unit.Items, memCacheTotalSize);

            Logger.InfoEvent("Flush", "Cache Size: {CacheSize} Total Channels: {TotalChannels}", memCacheTotalSize, _memCache.Keys.Count);
            var totalFlushed = 0;

            // A list of channels who have expired or have more than 1/5 the max total cache size
            var expiredSpecificChannels =
                _memCache.Where(x => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - x.Value.Pulled) > _expiration)
                    .Select(x => x.Key).Take(Math.Max(1, _memCache.Keys.Count / 5))
                    .ToArray();

            await expiredSpecificChannels.StartEachAsync(3, async (expired) =>
            {
                var messages = pullFromMemCache(expired.Channel, expired.Key, max: _flushSize);

                if (!messages.Any())
                    return;


                Logger.InfoEvent("ExpiredFlush", "{Flush} messages channel [{Channel}] key [{Key}]", messages.Length, expired.Channel, expired.Key);

                var translatedEvents = messages.Select(x => (IFullEvent)new FullEvent
                {
                    Descriptor = new EventDescriptor
                    {
                        EntityType = "DELAY",
                        StreamType = $"{_endpoint}.{StreamTypes.Delayed}",
                        Bucket = Assembly.GetEntryAssembly()?.FullName ?? "UNKNOWN",
                        StreamId = $"{expired.Channel}.{expired.Key}",
                        Timestamp = DateTime.UtcNow,
                        Headers = new Dictionary<string, string>()
                        {
                            ["Expired"] = "true",
                            ["FlushTime"] = DateTime.UtcNow.ToString("s"),
                            ["Instance"] = Defaults.Instance.ToString(),
                            ["Machine"] = Environment.MachineName,
                        }
                    },
                    Event = x,
                }).ToArray();
                try
                {
                    // Todo: might be a good idea to have a lock here so while writing to eventstore no new events can pile up


                    // Stream name to contain the channel, specific key, and the instance id
                    // it doesn't matter whats in the streamname, the category projection will queue it for execution anyway
                    // and a lot of writers to a single stream makes eventstore slow
                    var streamName = _streamGen(typeof(DelayedCache),
                        $"{_endpoint}.{StreamTypes.Delayed}", Assembly.GetEntryAssembly()?.FullName ?? "UNKNOWN",
                        $"{expired.Channel}.{expired.Key}", new Id[] { });
                    await _store.WriteEvents(streamName, translatedEvents, null).ConfigureAwait(false);
                    Interlocked.Add(ref totalFlushed, messages.Length);
                    Interlocked.Add(ref memCacheTotalSize, -messages.Length);

                }
                catch (Exception e)
                {
                    Logger.WarnEvent("FlushFailure", e, "Channel [{Channel}] key [{Key}]: {ExceptionType} - {ExceptionMessage}", expired.Channel, expired.Key, e.GetType().Name, e.Message);
                    // Failed to write to ES - put object back in memcache
                    addToMemCache(expired.Channel, expired.Key, messages);

                }
            }).ConfigureAwait(false);



            try
            {
                var limit = 10;
                while (memCacheTotalSize > _maxSize && limit > 0)
                {
                    if (memCacheTotalSize > (_maxSize * 1.5))
                    {
                        Logger.WarnEvent("TooLarge", "Pausing message processing");
                        Interlocked.CompareExchange(ref _tooLarge, 1, 0);
                    }

                    // Flush the largest channels
                    var toFlush = _memCache.Where(x => x.Value.Count > _flushSize || (x.Value.Count > (_maxSize / 5))).Select(x => x.Key).Take(Math.Max(1, _memCache.Keys.Count / 5)).ToArray();
                    // If no large channels, take some of the oldest
                    if (!toFlush.Any())
                        toFlush = _memCache.OrderBy(x => x.Value.Pulled).Select(x => x.Key).Take(Math.Max(1, _memCache.Keys.Count / 5)).ToArray();

                    await toFlush.StartEachAsync(3, async (expired) =>
                    {
                        var messages = pullFromMemCache(expired.Channel, expired.Key, max: _flushSize);

                        Logger.WarnEvent("LargeFlush", "{Flush} messages channel [{Channel}] key [{Key}]", messages.Length, expired.Channel, expired.Key);

                        var translatedEvents = messages.Select(x => (IFullEvent)new FullEvent
                        {
                            Descriptor = new EventDescriptor
                            {
                                EntityType = "DELAY",
                                StreamType = $"{_endpoint}.{StreamTypes.Delayed}",
                                Bucket = Assembly.GetEntryAssembly()?.FullName ?? "UNKNOWN",
                                StreamId = $"{expired.Channel}.{expired.Key}",
                                Timestamp = DateTime.UtcNow,
                                Headers = new Dictionary<string, string>()
                                {
                                    ["Expired"] = "false",
                                    ["FlushTime"] = DateTime.UtcNow.ToString("s"),
                                    ["Instance"] = Defaults.Instance.ToString(),
                                    ["Machine"] = Environment.MachineName,
                                }
                            },
                            Event = x,
                        }).ToArray();
                        try
                        {
                            // Todo: might be a good idea to have a lock here so while writing to eventstore no new events can pile up

                            var streamName = _streamGen(typeof(DelayedCache),
                            $"{_endpoint}.{StreamTypes.Delayed}",
                            Assembly.GetEntryAssembly()?.FullName ?? "UNKNOWN",
                            $"{expired.Channel}.{expired.Key}", new Id[] { });

                            await _store.WriteEvents(streamName, translatedEvents, null).ConfigureAwait(false);
                            Interlocked.Add(ref totalFlushed, messages.Length);
                            Interlocked.Add(ref memCacheTotalSize, -messages.Length);
                        }
                        catch (Exception e)
                        {
                            limit--;
                            Logger.WarnEvent("FlushFailure", e, "Channel [{Channel}] key [{Key}]: {ExceptionType} - {ExceptionMessage}", expired.Channel, expired.Key, e.GetType().Name, e.Message);
                            // Failed to write to ES - put object back in memcache
                            addToMemCache(expired.Channel, expired.Key, messages);
                            throw;
                        }


                    }).ConfigureAwait(false);


                }
            }
            catch (Exception e)
            {
                Logger.ErrorEvent("FlushException", e, "{ExceptionType} - {ExceptionMessage}", e.GetType().Name, e.Message);
            }
            finally
            {
                Interlocked.CompareExchange(ref _tooLarge, 0, 1);
            }
            
            Logger.InfoEvent("Flushed", "{Flushed} total", totalFlushed);
        }
    }
}
