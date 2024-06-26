﻿using System;
using System.Threading;
using Funq;
using NUnit.Framework;
using ServiceStack.Messaging;
using ServiceStack.RabbitMq;
using ServiceStack.Server.Tests.Caching;
using TestsConfig = ServiceStack.Server.Tests.Config;

namespace ServiceStack.Server.Tests.Messaging
{
    public interface IScopedDep
    {
    }
    public class ScopedDep : IScopedDep
    {
        private static int timesCalled;
        public static int TimesCalled
        {
            get => timesCalled;
            set => timesCalled = value;
        }

        public ScopedDep()
        {
            Interlocked.Increment(ref timesCalled);
        }
    }
    
    public class MqAppHost : AppSelfHostBase
    {
        public MqAppHost()
            : base(typeof(MqAppHost).Name, typeof(MqAppHostServices).Assembly) {}
        
        
#if !NETFRAMEWORK
        public override void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
                .AddScoped<IScopedDep, ScopedDep>(services);
        }
#endif
        

        public override void Configure(Container container)
        {
            var mqServer = new RabbitMqServer(connectionString: TestsConfig.RabbitMQConnString);

            mqServer.RegisterHandler<MqCustomException>(
                ExecuteMessage,
                HandleMqCustomException);

            mqServer.RegisterHandler<MqScopeDep>(ExecuteMessage);

            container.Register<IMessageService>(c => mqServer);
            mqServer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            Resolve<IMessageService>().Dispose();
            base.Dispose(disposing);
        }

        public CustomException LastCustomException;

        public void HandleMqCustomException(IMessageHandler mqHandler, IMessage<MqCustomException> message, Exception ex)
        {
            LastCustomException = ex.InnerException as CustomException;

            bool requeue = !(ex is UnRetryableMessagingException)
                && message.RetryAttempts < 1;

            if (requeue)
            {
                message.RetryAttempts++;
            }

            message.Error = ex.ToResponseStatus();
            mqHandler.MqClient.Nak(message, requeue: requeue, exception: ex);
        }
    }

    public class CustomException : Exception
    {
        public CustomException() {}
        public CustomException(string message) : base(message) {}
        public CustomException(string message, Exception innerException) : base(message, innerException) {}
    }

    public class MqCustomException
    {
        public string Message { get; set; }
    }

    public class MqScopeDep : IReturnVoid {}

    public class MqAppHostServices : Service
    {
        public static int TimesCalled = 0;

        public object Any(MqCustomException request)
        {
            TimesCalled++;
            throw new CustomException("ERROR: " + request.Message);
        }

#if !NETFRAMEWORK
        public void Any(MqScopeDep request)
        {
            var instance1 = Request.TryResolve<IScopedDep>();
            var instance2 = Request.ResolveScoped<IScopedDep>();
            if (instance1 != instance2)
                throw new Exception("instance1 != instance2");
        }
#endif
        
    }

    public class MqAppHostTests
    {
        private readonly MqAppHost appHost;

        public MqAppHostTests()
        {
            this.appHost = new MqAppHost();
            appHost
                .Init()
                .Start(Config.ListeningOn);
    }

        [OneTimeTearDown]
        public void TestFixtureTearDown()
        {
            appHost.Dispose();
        }

        [Test]
        public void Can_handle_custom_exception()
        {
            MqAppHostServices.TimesCalled = 0;

            using (var mqClient = appHost.TryResolve<IMessageService>().CreateMessageQueueClient())
            {
                mqClient.Publish(new MqCustomException { Message = "foo" });

                Thread.Sleep(1000);

                Assert.That(MqAppHostServices.TimesCalled, Is.EqualTo(2));
                Assert.That(appHost.LastCustomException.Message, Is.EqualTo("ERROR: foo"));
            }
        }

#if !NETFRAMEWORK
        [Test]
        public void Can_resolve_scoped_deps()
        {
            ScopedDep.TimesCalled = 0;
            
            using (var mqClient = appHost.TryResolve<IMessageService>().CreateMessageQueueClient())
            {
                mqClient.Publish(new MqScopeDep());

                Thread.Sleep(1000);

                Assert.That(ScopedDep.TimesCalled, Is.EqualTo(1));

                mqClient.Publish(new MqScopeDep());

                Thread.Sleep(1000);

                Assert.That(ScopedDep.TimesCalled, Is.EqualTo(2));
            }
        }
#endif
        
    }
}