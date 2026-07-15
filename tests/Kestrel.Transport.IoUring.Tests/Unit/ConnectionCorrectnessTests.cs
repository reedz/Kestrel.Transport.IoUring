using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ConnectionCorrectnessTests
{
    // --- Fix #1: Generation counter in UserData prevents stale CQE routing ---

    [Fact]
    public void EncodeDecodeUserData_WithGeneration_RoundTrips()
    {
        long connId = 42;
        ushort generation = 7;
        var opType = IoUringConnection.OpType.Send;

        ulong userData = IoUringConnection.EncodeUserData(connId, generation, opType);
        var (decodedId, decodedGen, decodedOp) = IoUringConnection.DecodeUserData(userData);

        decodedId.Should().Be(connId);
        decodedGen.Should().Be(generation);
        decodedOp.Should().Be(opType);
    }

    [Fact]
    public void EncodeDecodeUserData_DifferentGenerations_AreDistinguishable()
    {
        long connId = 100;
        var opType = IoUringConnection.OpType.Recv;

        ulong userData1 = IoUringConnection.EncodeUserData(connId, 1, opType);
        ulong userData2 = IoUringConnection.EncodeUserData(connId, 2, opType);

        userData1.Should().NotBe(userData2, "different generations must produce different UserData");

        var (_, gen1, _) = IoUringConnection.DecodeUserData(userData1);
        var (_, gen2, _) = IoUringConnection.DecodeUserData(userData2);

        gen1.Should().Be(1);
        gen2.Should().Be(2);
    }

    [Fact]
    public void EncodeDecodeUserData_GenerationWrapsAt65535()
    {
        long connId = 1;
        ushort maxGen = ushort.MaxValue; // 65535

        ulong userData = IoUringConnection.EncodeUserData(connId, maxGen, IoUringConnection.OpType.Send);
        var (_, gen, _) = IoUringConnection.DecodeUserData(userData);

        gen.Should().Be(65535, "generation should support full ushort range");
    }

    [Fact]
    public void EncodeDecodeUserData_LargeConnectionId_PreservesValue()
    {
        // With 40 bits for connectionId (shifted by 24), max is 2^40 - 1
        long connId = (1L << 39); // large but within range
        ushort generation = 100;

        ulong userData = IoUringConnection.EncodeUserData(connId, generation, IoUringConnection.OpType.Recv);
        var (decodedId, decodedGen, decodedOp) = IoUringConnection.DecodeUserData(userData);

        decodedId.Should().Be(connId);
        decodedGen.Should().Be(generation);
        decodedOp.Should().Be(IoUringConnection.OpType.Recv);
    }

    [Fact]
    public void EncodeDecodeUserData_AllOpTypes_RoundTrip()
    {
        foreach (IoUringConnection.OpType op in Enum.GetValues<IoUringConnection.OpType>())
        {
            ulong userData = IoUringConnection.EncodeUserData(1, 1, op);
            var (_, _, decoded) = IoUringConnection.DecodeUserData(userData);
            decoded.Should().Be(op, $"OpType {op} should round-trip");
        }
    }

    // --- Fix #2: Slot collision detection ---

    [Fact]
    public void ConnectionSlot_SequentialIds_CollidesWithoutGeneration()
    {
        // Demonstrates the collision that generation counters prevent
        int maxConnections = 36;

        long oldConnId = 1;
        long newConnId = 1 + maxConnections; // same slot

        int oldSlot = (int)(oldConnId % maxConnections);
        int newSlot = (int)(newConnId % maxConnections);

        oldSlot.Should().Be(newSlot, "sequential IDs collide in modular indexing");

        // But with different generations, stale CQEs are detected
        ushort oldGen = 1;
        ushort newGen = 2;

        ulong oldUserData = IoUringConnection.EncodeUserData(oldConnId, oldGen, IoUringConnection.OpType.Send);
        ulong newUserData = IoUringConnection.EncodeUserData(newConnId, newGen, IoUringConnection.OpType.Send);

        var (_, oldDecGen, _) = IoUringConnection.DecodeUserData(oldUserData);
        var (_, newDecGen, _) = IoUringConnection.DecodeUserData(newUserData);

        oldDecGen.Should().NotBe(newDecGen, "generation counter distinguishes stale CQEs");
    }

    // --- Fix #3: Ring queue depth is independent from connection capacity ---

    [Fact]
    public void EffectiveRingSize_DoesNotExceedConfiguredQueueDepth()
    {
        var options = new IoUringTransportOptions
        {
            MaxConnections = 1_000_000,
            ThreadCount = 28,
            RingSize = 256
        };

        options.EffectiveRingSize.Should().Be(256);
    }

    // --- Fix #4: UnsafeInlineScheduling option ---

    [Fact]
    public void UnsafeInlineScheduling_DefaultsToFalse()
    {
        var options = new IoUringTransportOptions();
        options.UnsafeInlineScheduling.Should().BeFalse(
            "blocking application code must not run on the IO loop unless explicitly enabled");
    }

    [Fact]
    public void UnsafeInlineScheduling_CanBeEnabled()
    {
        var options = new IoUringTransportOptions { UnsafeInlineScheduling = true };
        options.UnsafeInlineScheduling.Should().BeTrue(
            "users should be able to opt into inline scheduling for maximum throughput");
    }
}
