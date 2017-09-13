﻿using Aggregates;
using EventStore.ClientAPI;
using EventStore.ClientAPI.SystemData;
using Language;
using NLog;
using NServiceBus;
using NServiceBus.Features;
using NServiceBus.Pipeline;
using RabbitMQ.Client;
using StructureMap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Client
{
    class Endpoint
    {
        static readonly ManualResetEvent QuitEvent = new ManualResetEvent(false);
        private static IContainer _container;
        private static readonly NLog.ILogger Logger = LogManager.GetLogger("Client");

        private static void UnhandledExceptionTrapper(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Fatal(e.ExceptionObject);
            Console.WriteLine(e.ExceptionObject.ToString());
            Environment.Exit(1);
        }

        private static void ExceptionTrapper(object sender, FirstChanceExceptionEventArgs e)
        {
            Logger.Debug(e.Exception, "Thrown exception: {0}");
        }

        private static void Main(string[] args)
        {
            LogManager.GlobalThreshold = LogLevel.Info;

            AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionTrapper;
            AppDomain.CurrentDomain.FirstChanceException += ExceptionTrapper;


            ServicePointManager.UseNagleAlgorithm = false;
            var conf = NLog.Config.ConfigurationItemFactory.Default;

            var logging = new NLog.Config.LoggingConfiguration();

            var consoleTarget = new NLog.Targets.ColoredConsoleTarget();
            consoleTarget.Layout = "${date:universalTime=true:format=HH:mm:ss.fff} ${level:padding=-5:uppercase=true} ${logger:padding=-20:fixedLength=true} - ${message}";

            logging.AddTarget("console", consoleTarget);
            logging.AddRule(LogLevel.Debug, LogLevel.Fatal, consoleTarget);

            LogManager.Configuration = logging;


            NServiceBus.Logging.LogManager.Use<NLogFactory>();
            //EventStore.Common.Log.LogManager.SetLogFactory((name) => new EmbeddedLogger(name));

            // Give event store time to start
            Thread.Sleep(TimeSpan.FromSeconds(10));


            _container = new StructureMap.Container(x =>
            {
                x.Scan(y =>
                {
                    y.TheCallingAssembly();

                    y.WithDefaultConventions();
                });
            });

            var bus = InitBus().Result;
            _container.Configure(x => x.For<IMessageSession>().Use(bus).Singleton());

            var running = true;

            Console.WriteLine($"Use 'exit' to stop");
            do
            {
                Console.WriteLine("Please enter a message to send:");
                var message = Console.ReadLine();
                if (message.ToUpper() == "EXIT")
                    running = false;
                else
                {
                    bus.Send(new SayHello { Message = message });
                }

            } while (running);

            bus.Stop().Wait();
        }

        private static async Task<IEndpointInstance> InitBus()
        {
            NServiceBus.Logging.LogManager.Use<NLogFactory>();

            var endpoint = "client";

            var config = new EndpointConfiguration(endpoint);
            config.MakeInstanceUniquelyAddressable(Guid.NewGuid().ToString("N"));

            Logger.Info("Initializing Service Bus");


            config.EnableInstallers();
            config.LimitMessageProcessingConcurrencyTo(10);
            config.UseTransport<RabbitMQTransport>()
                //.CallbackReceiverMaxConcurrency(4)
                //.UseDirectRoutingTopology()
                .ConnectionString("host=localhost;Username=guest;Password=guest")
                .PrefetchMultiplier(5)
                .TimeToWaitBeforeTriggeringCircuitBreaker(TimeSpan.FromSeconds(30));

            config.UseSerialization<NewtonsoftSerializer>();

            config.UsePersistence<InMemoryPersistence>();
            config.UseContainer<StructureMapBuilder>(c => c.ExistingContainer(_container));

            if (Logger.IsDebugEnabled)
            {
                ////config.EnableCriticalTimePerformanceCounter();
                config.Pipeline.Register(
                    behavior: typeof(LogIncomingMessageBehavior),
                    description: "Logs incoming messages"
                );
            }

            config.Pipeline.Remove("LogErrorOnInvalidLicense");
            //config.EnableFeature<RoutedFeature>();
            config.DisableFeature<Sagas>();

            var client = await ConfigureStore();

            new Aggregates.Configure()
                .NServiceBus(config)
                .EventStore(new[] { client })
                .NewtonsoftJson();

            return await Aggregates.Bus.Start(config).ConfigureAwait(false);
        }

        public static async Task<IEventStoreConnection> ConfigureStore()
        {
            var connectionString = "host=localhost:2113;disk=false;path=C:/eventstore;embedded=false";
            var data = connectionString.Split(';');

            var hosts = data.Where(x => x.StartsWith("Host", StringComparison.CurrentCultureIgnoreCase));
            if (!hosts.Any())
                throw new ArgumentException("No Host parameter in eventstore connection string");


            var endpoints = hosts.Select(x =>
            {
                var addr = x.Substring(5).Split(':');
                if (addr[0] == "localhost")
                    return new IPEndPoint(IPAddress.Loopback, int.Parse(addr[1]));
                return new IPEndPoint(IPAddress.Parse(addr[0]), int.Parse(addr[1]));
            }).ToArray();

            var cred = new UserCredentials("admin", "changeit");
            var settings = EventStore.ClientAPI.ConnectionSettings.Create()
                .KeepReconnecting()
                .KeepRetrying()
                .SetGossipSeedEndPoints(endpoints)
                .SetClusterGossipPort(endpoints.First().Port - 1)
                .SetHeartbeatInterval(TimeSpan.FromSeconds(30))
                .SetGossipTimeout(TimeSpan.FromMinutes(5))
                .SetHeartbeatTimeout(TimeSpan.FromMinutes(5))
                .SetTimeoutCheckPeriodTo(TimeSpan.FromMinutes(1))
                .SetDefaultUserCredentials(cred);

            IEventStoreConnection client;
            if (hosts.Count() != 1)
            {
                var clusterSettings = EventStore.ClientAPI.ClusterSettings.Create()
                    .DiscoverClusterViaGossipSeeds()
                    .SetGossipSeedEndPoints(endpoints.Select(x => new IPEndPoint(x.Address, x.Port - 1)).ToArray())
                    .SetGossipTimeout(TimeSpan.FromMinutes(5))
                    .Build();

                client = EventStoreConnection.Create(settings, clusterSettings, "Domain");
            }
            else
                client = EventStoreConnection.Create(settings, endpoints.First(), "Domain");


            await client.ConnectAsync();

            return client;
        }

    }
    public class LogIncomingMessageBehavior : Behavior<IIncomingLogicalMessageContext>
    {
        public override Task Invoke(IIncomingLogicalMessageContext context, Func<Task> next)
        {

            Log.Debug("Received message '{0}'.\n" +
                            "ToString() of the message yields: {1}\n" +
                            "Message headers:\n{2}",
                            context.Message.MessageType != null ? context.Message.MessageType.AssemblyQualifiedName : "unknown",
                context.Message.Instance,
                string.Join(", ", context.MessageHeaders.Select(h => h.Key + ":" + h.Value).ToArray()));


            return next();

        }

        static readonly NLog.ILogger Log = LogManager.GetCurrentClassLogger();
    }
}
