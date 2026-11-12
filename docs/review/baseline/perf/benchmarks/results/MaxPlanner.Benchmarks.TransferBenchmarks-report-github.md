```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9974)
13th Gen Intel Core i7-13700HX 2.10GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.404
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  InvocationCount=1  
IterationCount=5  UnrollFactor=1  WarmupCount=1  

```
| Method                                          | Mean       | Error     | StdDev   | P95        | Gen0       | Gen1      | Gen2      | Allocated |
|------------------------------------------------ |-----------:|----------:|---------:|-----------:|-----------:|----------:|----------:|----------:|
| &#39;Export the fixture to JSON&#39;                    |   258.4 ms |  31.42 ms |  4.86 ms |   263.7 ms |  6000.0000 | 4000.0000 | 1000.0000 | 103.36 MB |
| &#39;Export the fixture to XLSX&#39;                    |   879.0 ms |  77.32 ms | 11.97 ms |   889.1 ms | 37000.0000 | 7000.0000 | 2000.0000 | 682.36 MB |
| &#39;Import the fixture JSON into an empty dataset&#39; | 1,649.6 ms | 215.08 ms | 55.86 ms | 1,724.9 ms | 31000.0000 | 7000.0000 | 1000.0000 | 487.58 MB |
