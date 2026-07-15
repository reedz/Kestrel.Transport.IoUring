using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class TransportOptionsTests
{
    private readonly IoUringTransportOptionsValidator _validator = new();

    [Fact]
    public void Defaults_AreValidAndUseKernelSupportedRingDepth()
    {
        var options = new IoUringTransportOptions();

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
        options.EffectiveRingSize.Should().Be(256);
        options.EffectiveRingSize.Should().BeLessThanOrEqualTo(IoUringTransportOptions.MaxRingEntries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(65536)]
    public void RingSize_MustBeSupportedPowerOfTwo(int ringSize)
    {
        var options = new IoUringTransportOptions { RingSize = ringSize };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains(nameof(options.RingSize)));
    }

    [Fact]
    public void BufferAllocation_MustFitManagedArray()
    {
        var options = new IoUringTransportOptions
        {
            BufferRingSize = 32768,
            ReceiveBufferSize = 65536,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains(nameof(options.ReceiveBufferSize)));
    }

    [Fact]
    public void PendingReceiveRingBudget_MustBePositive()
    {
        var options = new IoUringTransportOptions
        {
            MaxPendingReceiveBytesPerRing = 0,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(
            failure => failure.Contains(nameof(options.MaxPendingReceiveBytesPerRing)));
    }

    [Fact]
    public void DeferTaskRun_RequiresSingleIssuer()
    {
        var options = new IoUringTransportOptions
        {
            EnableDeferTaskRun = true,
            EnableSingleIssuer = false,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains(nameof(options.EnableSingleIssuer)));
    }

    [Fact]
    public void MultiListener_DistributesExactConnectionBudget()
    {
        var options = new IoUringTransportOptions
        {
            MaxConnections = 10,
            ThreadCount = 3,
        };

        var workers = Enumerable.Range(0, options.ThreadCount)
            .Select(index => IoUringMultiListener.CreateWorkerOptions(options, index))
            .ToArray();

        workers.Sum(worker => worker.MaxConnections).Should().Be(options.MaxConnections);
        workers.Select(worker => worker.MaxConnections).Should().Equal(4, 3, 3);
    }

    [Fact]
    public void MultiListener_PreservesSafetyAndDiagnosticOptions()
    {
        var options = new IoUringTransportOptions
        {
            MaxConnections = 8,
            ThreadCount = 2,
            UnsafeInlineScheduling = true,
            LogPoolStatsInterval = 17,
            MaxPendingReceiveBytesPerRing = 12345,
        };

        var worker = IoUringMultiListener.CreateWorkerOptions(options, 0);

        worker.UnsafeInlineScheduling.Should().BeTrue();
        worker.LogPoolStatsInterval.Should().Be(17);
        worker.MaxPendingReceiveBytesPerRing.Should().Be(12345);
    }

    [Fact]
    public void AddIoUringTransport_RegistersFactoryWithoutConfigureCallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIoUringTransport();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<IoUringTransportOptions>>().Value.Should().NotBeNull();
        provider.GetRequiredService<IConnectionListenerFactory>()
            .Should().BeOfType<IoUringTransportFactory>();
    }
}
