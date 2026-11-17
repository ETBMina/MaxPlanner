```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9974)
13th Gen Intel Core i7-13700HX 2.10GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.404
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  InvocationCount=1  
IterationCount=5  UnrollFactor=1  WarmupCount=1  

```
| Method                                          | Mean       | Error    | StdDev  | P95        | Gen0       | Gen1      | Gen2      | Allocated |
|------------------------------------------------ |-----------:|---------:|--------:|-----------:|-----------:|----------:|----------:|----------:|
| &#39;Export the fixture to JSON&#39;                    |   263.4 ms | 39.80 ms | 6.16 ms |   268.3 ms |  6000.0000 | 4000.0000 | 1000.0000 | 103.36 MB |
| &#39;Export the fixture to XLSX&#39;                    |   914.1 ms | 16.17 ms | 4.20 ms |   918.6 ms | 36000.0000 | 5000.0000 | 1000.0000 | 682.35 MB |
| &#39;Import the fixture JSON into an empty dataset&#39; | 1,628.2 ms | 56.09 ms | 8.68 ms | 1,635.4 ms | 31000.0000 | 7000.0000 | 1000.0000 | 487.46 MB |
