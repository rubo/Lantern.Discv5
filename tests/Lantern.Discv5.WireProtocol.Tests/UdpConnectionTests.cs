using System.Net.Sockets;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Logging.Exceptions;
using Lantern.Discv5.WireProtocol.Packet;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

public class UdpConnectionTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory;
    private Mock<ILogger<UdpConnection>> _mockLogger;
    private UdpClient _udpClient;

    [OneTimeSetUp]
    public void SetUp()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger<UdpConnection>>();
        _udpClient = new UdpClient();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(_mockLogger.Object);
    }

    [Test]
    public void CompleteMessageChannel_CompletesWithoutError()
    {
        var connectionOptions = new ConnectionOptions.Builder().WithPort(8081).WithReqRespTimeoutMs(1000).Build();
        var connection = new UdpConnection(connectionOptions, _mockLoggerFactory.Object);
        connection.CompleteMessageChannel();
    }

    [Test]
    public void Close_LogsMessageAndClosesClient()
    {
        var connectionOptions = new ConnectionOptions.Builder().WithPort(8082).WithReqRespTimeoutMs(1000).Build();
        var connection = new UdpConnection(connectionOptions, _mockLoggerFactory.Object);
        connection.Close();
    }

    [Test]
    public void Dispose_LogsMessageAndDisposesClient()
    {
        var connectionOptions = new ConnectionOptions.Builder().WithPort(8083).WithReqRespTimeoutMs(1000).Build();
        var connection = new UdpConnection(connectionOptions, _mockLoggerFactory.Object);
        connection.Dispose();
    }

    [Test]
    public void ValidatePacketSize_ThrowsExceptionForSmallPacket()
    {
        var data = new byte[PacketConstants.MinPacketSize - 1];
        Assert.Throws<InvalidPacketException>(() => UdpConnection.ValidatePacketSize(data));
    }

    [Test]
    public void ValidatePacketSize_ThrowsExceptionForLargePacket()
    {
        var data = new byte[PacketConstants.MaxPacketSize + 1];
        Assert.Throws<InvalidPacketException>(() => UdpConnection.ValidatePacketSize(data));
    }

    [Test]
    public void ValidatePacketSize_DoesNotThrowExceptionForValidPacket()
    {
        var data = new byte[PacketConstants.MaxPacketSize];
        UdpConnection.ValidatePacketSize(data);
    }
}