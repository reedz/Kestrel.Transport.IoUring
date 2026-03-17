namespace AspNetCoreUring.Transport;

public sealed class IoUringTransportOptions
{
    public int RingSize { get; set; } = 256;
    public int ThreadCount { get; set; } = Environment.ProcessorCount;
    public bool SubmitFromIOThread { get; set; } = true;
    public int MaxConnections { get; set; } = 1024;
}
