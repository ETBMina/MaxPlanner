```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9974)
13th Gen Intel Core i7-13700HX 2.10GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.404
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  

```
| Method                                                  | Mean     | Error     | StdDev    | P95      | Gen0     | Gen1     | Gen2    | Allocated  |
|-------------------------------------------------------- |---------:|----------:|----------:|---------:|---------:|---------:|--------:|-----------:|
| &#39;Save one task edit and see it in the projection store&#39; | 2.503 ms | 0.0461 ms | 0.0431 ms | 2.571 ms |   3.9063 |        - |       - |   71.21 KB |
| &#39;Dashboard recompute over the store&#39;                    | 3.163 ms | 0.0627 ms | 0.0671 ms | 3.258 ms | 238.2813 | 105.4688 | 15.6250 | 3917.28 KB |
