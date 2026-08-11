```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8655/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 5600G with Radeon Graphics 3.90GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3


```
| Method      | Capacity | Mean           | Error       | StdDev      | Ratio    | RatioSD | Allocated | Alloc Ratio |
|------------ |--------- |---------------:|------------:|------------:|---------:|--------:|----------:|------------:|
| **Write**       | **10000**    |      **4.2672 ns** |   **0.0457 ns** |   **0.0428 ns** |     **1.00** |    **0.01** |         **-** |          **NA** |
| Read        | 10000    |      0.7510 ns |   0.0129 ns |   0.0114 ns |     0.18 |    0.00 |         - |          NA |
| ScanChanged | 10000    |  5,718.0365 ns | 110.8500 ns | 136.1337 ns | 1,340.13 |   33.82 |         - |          NA |
|             |          |                |             |             |          |         |           |             |
| **Write**       | **20000**    |      **4.2750 ns** |   **0.0212 ns** |   **0.0188 ns** |     **1.00** |    **0.01** |         **-** |          **NA** |
| Read        | 20000    |      0.8094 ns |   0.0155 ns |   0.0121 ns |     0.19 |    0.00 |         - |          NA |
| ScanChanged | 20000    | 11,686.5687 ns | 137.0898 ns | 128.2339 ns | 2,733.75 |   31.27 |         - |          NA |
