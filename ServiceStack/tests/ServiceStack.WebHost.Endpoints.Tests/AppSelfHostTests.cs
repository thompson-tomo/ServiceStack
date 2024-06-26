﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Funq;
using NUnit.Framework;

namespace ServiceStack.WebHost.Endpoints.Tests;

public class SpinWait : IReturn<SpinWait>
{
    public int? Iterations { get; set; }
}

public class Sleep : IReturn<Sleep>
{
    public int? ForMs { get; set; }
}

public class PerfServices : Service
{
    private const int DefaultIterations = 1000 * 1000;
    private const int DefaultMs = 100;

    public object Any(SpinWait request)
    {
#if !NETFRAMEWORK
        int i = request.Iterations.GetValueOrDefault(DefaultIterations);
        //SpinWait.SpinUntil(i-- > 0);
#else
        Thread.SpinWait(request.Iterations.GetValueOrDefault(DefaultIterations));
#endif
        return request;
    }

    public object Any(Sleep request)
    {
        Thread.Sleep(request.ForMs.GetValueOrDefault(DefaultMs));
        return request;
    }
}

public class AppHostSmartPool() : AppHostHttpListenerSmartPoolBase("SmartPool Test", typeof(PerfServices).Assembly)
{
    public override void Configure(Container container)
    {
    }
}

[TestFixture, Ignore("Requires explicit admin privileges")]
public class AppSelfHostTests
{
    private readonly ServiceStackHost appHost;

    private readonly string ListeningOn;

    public AppSelfHostTests()
    {
        var port = HostContext.FindFreeTcpPort(startingFrom: 5000);
        if (port < 5000)
            throw new Exception("Expected port >= 5000, got: " + port);

        ListeningOn = "http://localhost:{0}/".Fmt(port);

        appHost = new AppHostSmartPool()
            .Init()
            .Start(ListeningOn);
    }

    [OneTimeTearDown]
    public void TestFixtureTearDown()
    {
        appHost.Dispose();
    }

    [Test]
    public void Can_call_SelfHost_Services()
    {
        var client = new JsonServiceClient(ListeningOn);

        client.Get(new Sleep { ForMs = 100 });
        client.Get(new SpinWait { Iterations = 1000 });
    }

    [Test]
    public async Task Can_call_SelfHost_Services_async()
    {
        var client = new JsonServiceClient(ListeningOn);

        var sleep = await client.GetAsync(new Sleep { ForMs = 100 });
        var spin = await client.GetAsync(new SpinWait { Iterations = 1000 });
    }
}