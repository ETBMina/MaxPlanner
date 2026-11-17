```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9974)
13th Gen Intel Core i7-13700HX 2.10GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.404
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  

```
| Method                                   | Mean     | Error   | StdDev  | P95      | Gen0      | Gen1      | Gen2      | Allocated |
|----------------------------------------- |---------:|--------:|--------:|---------:|----------:|----------:|----------:|----------:|
| &#39;Read snapshot of the fixture&#39;           | 185.4 ms | 3.67 ms | 4.37 ms | 191.9 ms | 4333.3333 | 3000.0000 | 1000.0000 |  54.98 MB |
| &#39;Projection store initial load to Ready&#39; | 249.1 ms | 4.93 ms | 4.84 ms | 255.2 ms | 6666.6667 | 4666.6667 | 1333.3333 |  91.73 MB |
