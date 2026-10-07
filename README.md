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
`--definition` принимает `.xdf`, `.a2l` и `.ecudef.json`; `--log pull.csv` (можно несколько) сравнивает лог VCDS/CSV с моделью.

Блоки чексумм описываются в `*.ecudef.json` (конец диапазона исключается):

```json
"checksums": [
  { "name": "Calibration ADD32", "start": "0x050000", "end": "0x070000", "algorithm": "Add32", "storedAt": "0x07FFF4" },
  { "name": "Code CRC32", "start": "0x000000", "end": "0x040000", "algorithm": "Crc32", "storedAt": "0x07FFF0" }
]
```

Поля: `storeSize` (1/2/4, по умолчанию 4), `endian` (`Big`/`Little`), `seed`, `complement`. Алгоритмы: `Add8`, `Add16`, `Add32`, `Crc16Ccitt`, `Crc32`.

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

* Чексуммы проверяются только по блокам, описанным в определении; реальные блоки EDC16U34 не зашиты, без описания статус `NotImplemented`. Коррекции чексумм нет.
* Определения: XDF, A2L (подмножество, неподдерживаемое перечисляется в заметках) и нативный JSON. DAMOS и OLS не читаются, нужен экспорт в A2L/XDF.
* Определение подключается через CLI `--definition` или каталог Definition DB; загрузки в проект через UI пока нет.
* Логи VCDS/CSV сравниваются со стационарной моделью; параметры модели по логу пока не калибруются.
* Демо-данные синтетические — это не реальная калибровка.
* Desktop-сборка не проверялась на Windows.
