# Benchmarks

`dotnet run -c Release --project benchmarks/ECUStudio.Benchmarks -- --filter '*'`
(для быстрого прогона добавьте `--job short`).

Вход: синтетический образ EDC16U34 512 КБ, stock vs Stage 1, сетка RPM шаг 500 × педаль {0, 50, 100}, одно уточнение.
Прогон 2026-10-07, short job, Linux x64 (облачный контейнер), .NET 10. Абсолютные числа зависят от машины;
важны порядки и отсутствие аллокаций в hot paths.

| Method                                 | Mean          | Allocated |
|--------------------------------------- |--------------:|----------:|
| Scan: Bosch map scanner (512 KB)         |   1,977.6 μs  |   11.5 KB |
| Extract: decode all defined maps       |      10.1 μs  |   19.0 KB |
| Interpolate: bilinear lookup           |      14.7 ns  |       0 B |
| Diff: byte + map diff                  |      59.5 μs  |    4.7 KB |
| Simulate: one operating point          |       3.0 μs  |    4.8 KB |
| Simulate: operating grid + scenarios   |     547.6 μs  |  647.2 KB |
| Risk: component margins                |      35.8 μs  |   26.7 KB |
| Full analysis pipeline                 |  17,537.8 μs  |   10.1 MB |

Full pipeline allocations are dominated by the 512 KB image copies of the synthetic generator inside the benchmark
(definition DB construction) and the report graph; the per-step paths above are the ones that scale with file count.
