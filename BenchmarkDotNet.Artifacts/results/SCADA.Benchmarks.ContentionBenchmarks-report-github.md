```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8655/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 5600G with Radeon Graphics 3.90GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  Job-CNUJVU : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

InvocationCount=1  UnrollFactor=1  

```
| Method         | Writers | Mean      | Error    | StdDev   | Median    | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------- |-------- |----------:|---------:|---------:|----------:|------:|--------:|----------:|------------:|
| **TagTable_Write** | **4**       |  **28.62 ns** | **0.620 ns** | **1.827 ns** |  **29.19 ns** |  **1.00** |    **0.09** |         **-** |          **NA** |
| Bus_Publish    | 4       |  76.26 ns | 1.880 ns | 5.484 ns |  75.67 ns |  2.68 |    0.26 |       4 B |          NA |
|                |         |           |          |          |           |       |         |           |             |
| **TagTable_Write** | **8**       |  **26.83 ns** | **0.510 ns** | **0.524 ns** |  **26.75 ns** |  **1.00** |    **0.03** |         **-** |          **NA** |
| Bus_Publish    | 8       | 116.69 ns | 2.309 ns | 2.920 ns | 116.76 ns |  4.35 |    0.14 |       4 B |          NA |
