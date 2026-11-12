```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9974)
13th Gen Intel Core i7-13700HX 2.10GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.404
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  

```
| Method                                   | Mean     | Error   | StdDev  | P95      | Gen0      | Gen1      | Gen2      | Allocated |
|----------------------------------------- |---------:|--------:|--------:|---------:|----------:|----------:|----------:|----------:|
| &#39;Read snapshot of the fixture&#39;           | 181.8 ms | 2.19 ms | 1.94 ms | 184.9 ms | 4333.3333 | 3000.0000 | 1000.0000 |  54.98 MB |
| &#39;Projection store initial load to Ready&#39; | 244.8 ms | 4.85 ms | 6.96 ms | 255.2 ms | 7333.3333 | 5333.3333 | 1666.6667 |  91.74 MB |
