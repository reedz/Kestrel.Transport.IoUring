```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.3 LTS (Noble Numbat)
AMD EPYC 7763, 1 CPU, 2 logical cores and 1 physical core
.NET SDK 10.0.102
  [Host]   : .NET 8.0.24 (8.0.2426.7010), X64 RyuJIT AVX2
  .NET 8.0 : .NET 8.0.24 (8.0.2426.7010), X64 RyuJIT AVX2

Job=.NET 8.0  Runtime=.NET 8.0  

```
| Method           | Mean     | Error   | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|----------------- |---------:|--------:|---------:|------:|--------:|----------:|------------:|
| SocketTransport  | 111.2 μs | 4.22 μs | 12.45 μs |  1.01 |    0.16 |   3.64 KB |        1.00 |
| IoUringTransport | 110.0 μs | 2.17 μs |  5.29 μs |  1.00 |    0.13 |   3.89 KB |        1.07 |
