using Watcher.Api.Delivery;

namespace Watcher.UnitTests;

public sealed class DeviceCommandTransportOptionsTests
{
    [Fact]
    public void Default_options_are_valid()
    {
        DeviceCommandTransportOptions options = new();

        Assert.Equal(TimeSpan.FromSeconds(5), options.SendTimeout);
        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void Send_timeout_outside_supported_range_is_rejected(int seconds)
    {
        DeviceCommandTransportOptions options = new()
        {
            SendTimeout = TimeSpan.FromSeconds(seconds)
        };

        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    public void Send_timeout_at_supported_bounds_is_valid(int seconds)
    {
        DeviceCommandTransportOptions options = new()
        {
            SendTimeout = TimeSpan.FromSeconds(seconds)
        };

        Assert.True(options.IsValid());
    }

    [Fact]
    public void Acknowledgement_timeout_remains_independent_from_send_timeout()
    {
        DeviceCommandTransportOptions options = new()
        {
            SendTimeout = TimeSpan.FromSeconds(1),
            AcknowledgementTimeout = TimeSpan.FromMinutes(1)
        };

        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(501)]
    public void Batch_size_outside_supported_range_is_rejected(int batchSize)
    {
        DeviceCommandTransportOptions options = new() { BatchSize = batchSize };

        Assert.False(options.IsValid());
    }

    [Fact]
    public void Maximum_retry_delay_cannot_be_shorter_than_initial_delay()
    {
        DeviceCommandTransportOptions options = new()
        {
            InitialRetryDelay = TimeSpan.FromSeconds(20),
            MaximumRetryDelay = TimeSpan.FromSeconds(15)
        };

        Assert.False(options.IsValid());
    }

    [Fact]
    public void Poll_interval_must_be_within_bounds()
    {
        DeviceCommandTransportOptions options = new()
        {
            PollInterval = TimeSpan.FromMilliseconds(25)
        };

        Assert.False(options.IsValid());
    }
}
