using System.Net;
using Microsoft.AspNetCore.Http;
using WotBTreader.Host.Web.Infrastructure;

namespace WotBTreader.Host.Web.Tests;

/// <summary>
/// The loopback gate is the outer half of the local trust boundary. These cases
/// pin the rebinding, cross-origin, and address checks that keep another local
/// program or a hostile page from driving the API.
/// </summary>
[TestClass]
public sealed class LoopbackOnlyMiddlewareTests
{
    [TestMethod]
    [DataRow("localhost")]
    [DataRow("LOCALHOST")]
    [DataRow("127.0.0.1")]
    [DataRow("127.5.5.5")]
    [DataRow("::1")]
    [DataRow("[::1]")]
    public void LoopbackHostsAreRecognized(string host) =>
        Assert.IsTrue(
            LoopbackOnlyMiddleware.IsLoopbackHost(host),
            $"'{host}' addresses this machine and must be treated as loopback.");

    [TestMethod]
    [DataRow("")]
    [DataRow("example.com")]
    [DataRow("localhost.example.com")]
    [DataRow("127.0.0.1.example.com")]
    [DataRow("10.0.0.1")]
    [DataRow("0.0.0.0")]
    public void NonLoopbackHostsAreRejected(string host) =>
        Assert.IsFalse(
            LoopbackOnlyMiddleware.IsLoopbackHost(host),
            $"'{host}' does not identify this machine and must be rejected.");

    [TestMethod]
    public void LocalRequestWithoutOriginIsAllowed()
    {
        HttpRequest request = CreateRequest(IPAddress.Loopback, "localhost", 5000, origin: null);

        Assert.IsTrue(LoopbackOnlyMiddleware.IsAllowed(request));
    }

    [TestMethod]
    public void LocalRequestWithMatchingOriginIsAllowed()
    {
        HttpRequest request = CreateRequest(
            IPAddress.Loopback,
            "localhost",
            5000,
            "http://localhost:5000");

        Assert.IsTrue(LoopbackOnlyMiddleware.IsAllowed(request));
    }

    [TestMethod]
    public void RemoteAddressIsRejected()
    {
        HttpRequest request = CreateRequest(
            IPAddress.Parse("192.168.1.20"),
            "localhost",
            5000,
            origin: null);

        Assert.IsFalse(LoopbackOnlyMiddleware.IsAllowed(request));
    }

    [TestMethod]
    public void RebindingHostHeaderIsRejected()
    {
        // A DNS rebinding attack arrives on the loopback socket but names an
        // attacker-controlled host, which keeps the browser's same-origin view.
        HttpRequest request = CreateRequest(
            IPAddress.Loopback,
            "attacker.example.com",
            5000,
            origin: null);

        Assert.IsFalse(LoopbackOnlyMiddleware.IsAllowed(request));
    }

    [TestMethod]
    public void OriginFromAnotherLocalPortIsRejected()
    {
        // Another local web site must not drive mutations with ambient cookies.
        HttpRequest request = CreateRequest(
            IPAddress.Loopback,
            "localhost",
            5000,
            "http://localhost:5001");

        Assert.IsFalse(LoopbackOnlyMiddleware.IsAllowed(request));
    }

    [TestMethod]
    [DataRow("http://example.com")]
    [DataRow("https://example.com:5000")]
    [DataRow("file:///c:/tmp")]
    [DataRow("null")]
    public void HostileOriginsAreRejected(string origin)
    {
        HttpRequest request = CreateRequest(IPAddress.Loopback, "localhost", 5000, origin);

        Assert.IsFalse(LoopbackOnlyMiddleware.IsAllowed(request));
    }

    [TestMethod]
    public void MissingRemoteAddressIsRejected()
    {
        DefaultHttpContext context = new();
        context.Request.Host = new HostString("localhost", 5000);

        Assert.IsFalse(LoopbackOnlyMiddleware.IsAllowed(context.Request));
    }

    private static HttpRequest CreateRequest(
        IPAddress remoteAddress,
        string host,
        int port,
        string? origin)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Request.Host = new HostString(host, port);
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        return context.Request;
    }
}
