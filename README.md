# ECUStudio

Инженерный анализ прошивок ECU (`.bin` + VIN) для чип-тюнинг-экспертизы без стенда: идентификация блока и авто,
карты и их изменения, физическая симуляция с диапазонами и confidence, запасы по компонентам, мультиагентный разбор
Claude поверх структурированного контекста. Первый блок: **Bosch EDC16U34, VW 1.9 TDI PD**.

> Все числа — инженерные оценки с диапазоном и уверенностью, не измерения. Неизвестный предел — `UNKNOWN`, не `SAFE`.

Архитектура и правила: [ARCHITECTURE.md](ARCHITECTURE.md).

## Требования

* .NET SDK 10
* Node.js 20+ (только для сборки UI)
* PostgreSQL 15+ (сервер) — опционально; desktop и dev используют SQLite
* `ANTHROPIC_API_KEY` — опционально; без него AI-слой отвечает 503, остальное работает

## Быстрый старт

```bash
# UI → wwwroot (один раз или после правок фронтенда)
cd frontend && npm ci && npm run export:desktop && cd ..

# сервер на http://localhost:5080 (SQLite в %LOCALAPPDATA%/ECUStudio или ~/.local/share/ECUStudio)
dotnet run --project src/ECUStudio.Api
```

Откройте http://localhost:5080 → «Demo project»: синтетические stock / Stage 1 файлы и полный анализ.

Разработка UI с hot reload: `dotnet run --project src/ECUStudio.Api` + `cd frontend && npm run dev` → http://localhost:3000
(CORS для localhost:3000 включён в Development). После изменения контрактов API: `npm run gen:api`.

## Конфигурация

| Переменная / ключ | По умолчанию | |
|---|---|---|
| `ECUSTUDIO_STORAGE` / `EcuStudio:Storage` | `sqlite` | `memory` \| `sqlite` \| `postgres` |
| `ECUSTUDIO_CONNECTION` / `ConnectionStrings:Sqlite` \| `ConnectionStrings:Postgres` | SQLite в профиле пользователя | для postgres — строка Npgsql (обязательна) |
| `ECUSTUDIO_DEFINITIONS` / `EcuStudio:DefinitionsPath` | `definitions` | каталог `*.ecudef.json` (Definition DB по SW) |
| `ANTHROPIC_API_KEY` | — | включает Claude |
| `ECUSTUDIO_CLAUDE_MODEL` | `claude-opus-5-5` | модель агентов |

Миграции схемы применяются при старте автоматически (SQL в `src/ECUStudio.Infrastructure/Migrations`).

## CLI

```bash
cli="dotnet run --project src/ECUStudio.Cli --"
$cli demo ./demo                                       # SYNTHETIC stock / stage1 / aggressive + demo.ecu
$cli identify demo/stage1_synthetic.bin
$cli analyze demo/stage1_synthetic.bin --stock demo/stock_synthetic.bin --vin WVWZZZ1KZ6W123456 \
     --transmission trans_dsg_dq250 --fail-on warning -o report.md        # exit 3, если риск ≥ порога
$cli compare demo/stock_synthetic.bin demo/stage1_aggressive_synthetic.bin
$cli simulate demo/demo.ecu
```

`--json` выводит полный `AnalysisReport`, `--definition file.xdf` подключает внешнее определение карт.

## Desktop (Windows)

`./build/publish-desktop.ps1` → `artifacts/desktop/ECUStudio.exe` (self-contained, нужен WebView2 Runtime).
Данные: `%LOCALAPPDATA%/ECUStudio/ecustudio.db`. API поднимается на случайном loopback-порту внутри процесса.

## Тесты и бенчмарки

```bash
dotnet test                                                              # unit + pipeline + SQLite + HTTP
dotnet run -c Release --project benchmarks/ECUStudio.Benchmarks -- --filter '*'
```

Результаты бенчмарков: [docs/benchmarks.md](docs/benchmarks.md).

## Что сейчас не сделано

* Контрольные суммы EDC16U34 не считаются (статус `NotImplemented`) — файл после правок не готов к записи в блок.
* Импорт A2L / DAMOS / OLS — заглушки; работают XDF и нативный JSON.
* Раздел Logs (диагностические логи) не реализован.
* Демо-данные синтетические — это не реальная калибровка.
* Desktop-сборка не проверялась на Windows.
