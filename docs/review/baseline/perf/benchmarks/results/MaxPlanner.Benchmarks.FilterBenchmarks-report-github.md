```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9974)
13th Gen Intel Core i7-13700HX 2.10GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.404
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  

```
| Method                                          | Filter          | Mean       | Error    | StdDev   | P95        | Gen0    | Gen1   | Allocated |
|------------------------------------------------ |---------------- |-----------:|---------:|---------:|-----------:|--------:|-------:|----------:|
| **&#39;Filter and search every task and Subtask card&#39;** | **Search**          | **2,170.4 μs** | **36.43 μs** | **34.08 μs** | **2,217.5 μs** | **35.1563** | **3.9063** |  **577.3 KB** |
| **&#39;Filter and search every task and Subtask card&#39;** | **Fields**          |   **303.1 μs** |  **2.97 μs** |  **2.78 μs** |   **306.9 μs** | **26.3672** | **4.3945** | **407.21 KB** |
| **&#39;Filter and search every task and Subtask card&#39;** | **SearchAndFields** |   **566.6 μs** |  **9.50 μs** |  **8.88 μs** |   **579.9 μs** | **27.3438** | **4.8828** | **434.11 KB** |
