# ECUStudio

Инженерный анализ прошивок ECU (`.bin` + VIN) для чип-тюнинг-экспертизы без стенда: идентификация блока и авто,
карты и их изменения, физическая симуляция с диапазонами и confidence, запасы по компонентам, мультиагентный разбор
Claude поверх структурированного контекста. Первый блок: **Bosch EDC16U34, VW 1.9 TDI PD**.

> Все числа — инженерные оценки с диапазоном и уверенностью, не измерения. Неизвестный предел — `UNKNOWN`, не `SAFE`.

Архитектура и правила: [ARCHITECTURE.md](ARCHITECTURE.md).

## Запуск на своей машине

Нужно: [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0), [Node.js 22](https://nodejs.org) (20+ тоже подойдёт, нужен только
для сборки UI) и git. PostgreSQL не нужен: по умолчанию используется SQLite. `ANTHROPIC_API_KEY` опционален, без него
AI-разбор отвечает 503, всё остальное работает.

```bash
git clone -b feature/ecustudio-dotnet https://github.com/prorokallaha/ECUStudio.git   # пока код в PR #1
cd ECUStudio
dotnet test                                    # проверка окружения: все тесты должны пройти

# 1. собрать UI в wwwroot API (один раз и после правок фронтенда)
cd frontend && npm ci && npm run export:desktop && cd ..

# 2. запустить сервер (http://localhost:5080)
dotnet run --project src/ECUStudio.Api
```

Откройте http://localhost:5080 и нажмите «Demo project (synthetic)»: появятся синтетические stock / Stage 1 / aggressive прошивки,
лог и полный анализ. Свои файлы: «New project» и загрузка `.bin`. База SQLite лежит в `%LOCALAPPDATA%\ECUStudio`
(Windows), `~/.local/share/ECUStudio` (Linux) или `~/Library/Application Support/ECUStudio` (macOS).

Демо-прошивки файлами (для CLI или загрузки в UI) создаёт CLI: `dotnet run --project src/ECUStudio.Cli -- demo ./demo`.
Это синтетические образы, не реальная калибровка.

### Desktop (Windows)

В PowerShell 7 из корня репозитория:

```powershell
./build/publish-desktop.ps1        # UI → wwwroot, тесты, publish → artifacts/desktop/ECUStudio.exe
./artifacts/desktop/ECUStudio.exe
```

Нужен Microsoft Edge WebView2 Runtime (в Windows 10/11 обычно уже установлен). API поднимается внутри процесса
на случайном loopback-порту, данные в `%LOCALAPPDATA%\ECUStudio\ecustudio.db`. Desktop пока не проверялся на живой Windows.

### Разработка UI с hot reload

```bash
dotnet run --project src/ECUStudio.Api                                   # терминал 1, API на :5080
cd frontend && NEXT_PUBLIC_API_BASE=http://localhost:5080 npm run dev     # терминал 2, http://localhost:3000
```

В PowerShell: `$env:NEXT_PUBLIC_API_BASE="http://localhost:5080"; npm run dev`. CORS для localhost:3000 включён в
Development. После изменения контрактов API: `npm run gen:api` (при запущенном API).

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
