```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9974)
13th Gen Intel Core i7-13700HX 2.10GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.404
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  

```
| Method                                          | Filter          | Mean       | Error    | StdDev   | P95        | Gen0    | Gen1    | Allocated  |
|------------------------------------------------ |---------------- |-----------:|---------:|---------:|-----------:|--------:|--------:|-----------:|
| **&#39;Filter and search every task and Subtask card&#39;** | **Search**          | **2,241.7 μs** | **44.14 μs** | **88.15 μs** | **2,379.6 μs** | **82.0313** | **11.7188** | **1293.55 KB** |
| **&#39;Filter and search every task and Subtask card&#39;** | **Fields**          |   **305.5 μs** |  **4.02 μs** |  **3.56 μs** |   **311.9 μs** | **26.3672** |  **4.3945** |  **407.21 KB** |
| **&#39;Filter and search every task and Subtask card&#39;** | **SearchAndFields** |   **617.8 μs** | **12.22 μs** | **12.00 μs** |   **633.4 μs** | **35.1563** |  **6.8359** |  **548.61 KB** |
