```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9974)
13th Gen Intel Core i7-13700HX 2.10GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.404
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  

```
| Method                                                  | Mean     | Error     | StdDev    | P95      | Gen0     | Gen1     | Gen2    | Allocated  |
|-------------------------------------------------------- |---------:|----------:|----------:|---------:|---------:|---------:|--------:|-----------:|
| &#39;Save one task edit and see it in the projection store&#39; | 1.675 ms | 0.0210 ms | 0.0196 ms | 1.702 ms |   3.9063 |   1.9531 |       - |   71.19 KB |
| &#39;Dashboard recompute over the store&#39;                    | 3.190 ms | 0.0366 ms | 0.0342 ms | 3.237 ms | 238.2813 | 105.4688 | 15.6250 | 3917.28 KB |
