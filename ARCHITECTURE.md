# ECUStudio — архитектура

Система инженерного анализа прошивок ECU (`.bin` + VIN) для профессиональной чип-тюнинг-экспертизы без стенда.
Первый поддерживаемый блок — Bosch EDC16U34 (VW 1.9 TDI PD: BKC/BXE/BLS/BJB).

Приоритеты, по которым принимались решения: **корректность > прослеживаемость > производительность > расширяемость > UI**.

## 1. Главные инварианты

| Инвариант | Где обеспечивается |
|---|---|
| Любое расчётное число — `Estimate` (значение + диапазон + confidence + `ValueKind`), а не точка | `ECUStudio.Core/Estimate.cs` |
| Отображение округляется до разрешения модели (145 hp, не 144.63) | `EngineeringRounding`, фронт `engRound` |
| Неизвестный предел → `UNKNOWN`, никогда `SAFE`. `SAFE` при критических неизвестных запрещён | `RiskEngine`, `AIResponseValidator.GuardSafe` |
| Неприменимое (давление рейки на PD) → `NotApplicable`, а не 0 | `IEcuPlugin.HasCommonRail`, `RiskEngine` |
| AI получает только структурированный контекст, не бинарь | `AIContextBuilder` |
| Утверждение AI без ссылки на доказательство из индекса контекста отклоняется | `AIResponseValidator.ParseFindings` |
| AI не может понизить серьёзность, полученную из физики; сам по себе эскалирует максимум до `WARNING` | `ConsensusEngine.Combine` |
| Неизвестная карта — кандидат, пока человек не подтвердит (CONFIRM / REJECT / EDIT DEFINITION) | `MapCandidate`, `candidate_decisions` |
| Контрольные суммы: честный `NotImplemented`, а не «Valid» | `Edc16U34Plugin.VerifyChecksums` |

## 2. Структура решения

```
ECUStudio.sln
├─ src/
│  ├─ ECUStudio.Core            Estimate, Param, Severity, Finding/Evidence, ошибки, хеширование, JSON-опции
│  ├─ ECUStudio.Binary          BinaryImage, типы данных/endianness, diff, поиск по шаблону, секции памяти, CRC
│  ├─ ECUStudio.Calibration     модель карт, интерполяция, сканер Bosch-карт, классификатор, импорт XDF/JSON,
│  │                            плагины ECU (IEcuPlugin, Edc16U34Plugin), diff карт, детектор аномалий, граф зависимостей
│  ├─ ECUStudio.Vehicle         VIN-декодер (ISO 3779 + VAG), база вариантов авто, VehicleResolver
│  ├─ ECUStudio.Components      каталог компонентов (двигатель, турбина, форсунки, КПП…), HardwareProfile, overrides
│  ├─ ECUStudio.Simulation      физическая модель, ансамбль моделей момента, OperatingGrid, VirtualDyno
│  ├─ ECUStudio.Risk            запасы по компонентам, итоговый вердикт, объяснения
│  ├─ ECUStudio.AI              IAIProvider, ClaudeProvider, агенты, валидация, консенсус, оркестратор, кэш
│  ├─ ECUStudio.Application     AnalysisPipeline, StudioService (use-cases), проекты, отчёты, синтетические бинарники
│  ├─ ECUStudio.Infrastructure  EF Core (PostgreSQL/SQLite), SQL-миграции, composition root AddEcuStudio()
│  ├─ ECUStudio.Api             ASP.NET Core minimal API + раздача статического фронтенда
│  ├─ ECUStudio.Cli             `ecustudio identify | analyze | compare | simulate | demo`
│  └─ ECUStudio.Desktop         WinForms + WebView2, self-contained ECUStudio.exe (хостит Api на loopback)
├─ tests/ECUStudio.Tests        xUnit: unit + pipeline + SQLite + HTTP-интеграция
├─ benchmarks/ECUStudio.Benchmarks  BenchmarkDotNet
├─ frontend/                    Next.js (static export) → src/ECUStudio.Api/wwwroot
└─ research/                    единственное место, где допустим Python (прототипы, не прод)
```

Зависимости строго внутрь: `Core ← Binary ← Calibration ← Simulation ← Risk ← Application ← Infrastructure ← Api/Cli/Desktop`.
`Vehicle`, `Components`, `AI` зависят только от `Core` (и друг от друга по необходимости). Хосты не содержат бизнес-логики:
каждый endpoint и CLI-команда делегирует в `StudioService` / `AnalysisPipeline`.

## 3. Пайплайн анализа

```
BIN (+stock BIN, +VIN, +definition, +hardware overrides)
 │
 ├─ identify      PluginRegistry.Detect → IEcuPlugin (скоринг, причины; ниже порога — UNSUPPORTED_ECU 422)
 ├─ read          Identify: SW/HW/part номера, секции памяти, endianness; VerifyChecksums
 ├─ maps          ResolveDefinitions: DefinitionDb (по SW) → внешнее определение (XDF/JSON) → сигнатурный скан
 │                (кандидаты с гипотезами ролей) → подтверждённые пользователем кандидаты
 ├─ stock         MapDiffer: побайтовый diff + diff карт; изменения вне карт с привязкой к секции (Code/Calibration)
 │                AnomalyDetector: PERCENTAGE_TUNING, LIMITER_MAXED, CLIPPING, FLAT_MAP, DISCONTINUITY,
 │                AXIS_CHANGED, OUTLIER_CHANGE, CODE_SECTION_CHANGED, UNMAPPED_CHANGES; Stage1ConsistencyAnalyzer
 ├─ vehicle       VinDecoder + ECU-идентификация → ранжированные варианты → VehicleProfile
 ├─ components    HardwareProfile из каталога + overrides пользователя (с source/confidence)
 ├─ dependencies  граф: карты ↔ физические величины (Driver Wish → Torque Limiter → Torque→IQ → Smoke → …)
 ├─ simulation    OperatingGrid: грубая сетка RPM×педаль + адаптивное уточнение на смене лимитера;
 │                WOT-сценарии: высота 1500/3000 м, жара, холод, передачи
 └─ risk          RiskEngine: нагрузка vs предел по каждому компоненту → вердикт + CriticalUnknowns
 │
 └─ AnalysisReport (JSON, хранится целиком) + MarkdownReport
```

Прогресс шагов публикуется как `StepProgress` → `JobTracker` → SSE (`/api/v1/jobs/{id}/events`), поздние подписчики
получают историю.

### Физическая модель (Simulation)

Стационарная модель для дизельных ECU, управляемых моментом. Проходит цепочку ECU: driver wish → лимитеры момента →
torque→IQ → smoke limiter (по воздуху), и воздушный тракт: boost target/limiter/SVBL → давление, PR компрессора,
температура после компрессора/интеркулера, массовый расход воздуха, λ. Момент оценивается **четырьмя независимыми моделями**:

| | Модель | Чего требует |
|---|---|---|
| A | Torque structure: обратная к OEM torque→IQ | карта torque→IQ |
| B | Энергия топлива: ṁf·LHV·ηb | IQ, оценка КПД |
| C | Воздух: масса воздуха при типичной λ полной нагрузки | boost, VE |
| D | Эмпирическая для известного двигателя (OEM Nm/mg) | спецификация двигателя |

`SimulationEngine.Ensemble`: взвешенное по confidence среднее; полуширина интервала = √(внутренняя² + (разброс/2)²);
confidence падает с относительным разбросом моделей; одна модель — ×0.75; всё ограничено `ConfidenceCap`
(доступность данных). За пределами откалиброванного диапазона карты cap ≤ 0.2. Каждая точка хранит `Trace`
(формула, значение, карта) — его показывает инспектор рабочей точки.

### Риск

`ComponentMargin` на компонент: нагрузка (`Estimate`), предел (`Param` c источником), utilization/margin, изменение
к стоку, `LoadLevel`, severity, confidence, evidence, условия худшего случая (rpm, высота, температура).

* Предел известен → severity по utilization; `ShowExactUtilization` только если и нагрузка, и предел надёжны.
* Предел неизвестен → `UNKNOWN` + относительная нагрузка к стоку как вспомогательное свидетельство.
* Неприменимо → `Critical = false`, на вердикт не влияет.
* Итог = худший критический компонент; `UNKNOWN` ранжируется выше `REVIEW`, ниже `WARNING`.

## 4. Модели данных (основные)

* `Estimate { Value, Low, High, Unit, Confidence, Kind: Estimated|EcuRequested|Measured|Spec|Assumed|Unknown|NotApplicable }`
* `Param { Number|Text, Unit, Source: OemSpec|PublicSpec|DefinitionDb|Xdf|…|User|Unknown, Confidence, UserVerified }`
* `Finding { Code, Text, Severity, Confidence, Evidence[], Assumptions[], Unknowns[], AffectedComponents[], RelatedMaps[], Source: Rule|Physics|AI }`
* `Evidence { Type: Map|Diff|Physics|Rule|Spec|…, Ref, Detail }` — `Ref` обязан существовать в индексе контекста.
* `MapDefinition` / `CalibrationMap` / `CalibrationSet`, `MapCandidate { Hypotheses[], Status, ConfirmedRole }`
* `Project` — агрегат (файлы, overrides, preferred variant, трансмиссия), хранится одним JSON-документом.
* `AnalysisReport` — полный результат анализа, неизменяемый, версия `AnalysisVersion`.

## 5. Хранилище

Composition root `AddEcuStudio(EcuStudioOptions)`; `Storage = memory | sqlite | postgres`.

* **PostgreSQL** — сервер. **SQLite** — desktop (`%LOCALAPPDATA%/ECUStudio/ecustudio.db`). **memory** — CLI и тесты.
* Миграции — версионированные SQL-файлы, встроенные в сборку (`Migrations/{postgres,sqlite}/NNNN_name.sql`),
  применяются `SchemaMigrator` ровно один раз, учёт в `schema_migrations`; на PostgreSQL под advisory lock
  (несколько инстансов могут стартовать одновременно). Выбор plain SQL вместо EF-миграций: схема читаема DBA,
  один механизм для двух провайдеров, нет дрейфа снапшотов.

| Таблица | Назначение |
|---|---|
| `projects` | id, name, vin, created/updated, `document` (jsonb/TEXT) — агрегат проекта |
| `file_contents` | бинарники по file_id (bytea/BLOB), sha256, size |
| `analyses` | отчёт (jsonb), `analysis_version`, FK на проект (cascade) |
| `candidate_decisions` | confirm/reject кандидатов карт по (sha256, address), роль, заметка |
| `ai_cache` | ответ агента + usage, ключ = hash(bin, stock, профиль авто, версия анализа, агент+инструкция, контекст, модель) |

Trade-off: документное хранение агрегата проще эволюционирует, чем нормализованная схема, но не даёт SQL-запросов по
внутренностям отчёта. Для целевых сценариев (открыть проект/отчёт) это не нужно; если понадобится аналитика по
парку прошивок — добавить проекции (materialized columns / отдельные таблицы) новой миграцией.

## 6. API (prefix `/api/v1`)

| Метод | Путь | Что делает |
|---|---|---|
| GET | `/info`, `/vin/{vin}`, `/components?kind=`, `/variants` | справочники |
| GET/POST | `/projects`, `/projects/demo` | список / создать / демо-проект |
| GET/PUT/DELETE | `/projects/{id}` | проект |
| POST | `/projects/{id}/files` (multipart ≤ 16 МБ) | загрузка BIN |
| PUT | `/projects/{id}/files/{fileId}/role` | stock / modified / version |
| PUT / DELETE | `/projects/{id}/hardware`, `/hardware/{kind}` | overrides железа |
| POST | `/projects/{id}/analyses` → 202 `{jobId}` | запуск анализа |
| GET | `/jobs/{id}`, `/jobs/{id}/events` (SSE) | состояние / поток прогресса |
| GET | `/analyses/{id}`, `/report.md`, `/maps/{mapId}`, `/hex`, `/search` | отчёт, карта, hex-страницы, поиск |
| POST | `/analyses/{id}/dyno`, `/inspect` | виртуальный стенд, трассировка рабочей точки |
| POST | `/analyses/{id}/candidates/{cid}/decision` | CONFIRM / REJECT / EDIT |
| POST/GET | `/analyses/{id}/ai`, `/ai/ask`, `/candidates/{cid}/hypotheses` | мультиагентный разбор, вопрос, гипотезы |

Ошибки единообразно: `{"error":{"code","message","details"}}` (`INVALID_VIN` 400, `NOT_FOUND` 404,
`UNSUPPORTED_ECU`/`DEFINITION_ERROR` 422, `AI_UNAVAILABLE` 503, `AI_RESPONSE_INVALID` 502). Доменные ошибки не
логируются как серверные сбои. OpenAPI: `/api/openapi/v1.json`; не-nullable свойства помечаются `required`, поэтому
сгенерированные TS-типы (`npm run gen:api`) совпадают с форматом на проводе.

## 7. AI-слой

* `IAIProvider` — провайдер-нейтральный интерфейс; `ClaudeProvider` (Anthropic SDK, структурированный JSON-вывод по
  схеме, effort по агенту, prompt caching системного промпта и контекста). Ключ — только `ANTHROPIC_API_KEY`; без него `UnconfiguredAIProvider`
  честно отвечает 503, вся физика/diff/риск работают без AI.
* Контекст (`AIContextBuilder`): детерминированный JSON (идентификация, профиль авто, изменённые карты, аномалии,
  WOT, сценарии, компоненты риска, неизвестные) + индекс доказательств (`AllowedRefs`). Системный промпт и контекст
  одинаковы для всех агентов → общий кэшируемый префикс; инструкция агента идёт после breakpoint.
* Агенты: Calibration, Engine, Turbo, Fuel System, Thermal, Drivetrain (параллельно, `AIMaxParallel`) →
  Verifier (независимая проверка каждого WARNING/DANGER: SUPPORTED/CONTRADICTED/INSUFFICIENT) → Safety Reviewer
  (видит выводы всех аналитиков) → консенсус с физикой по компонентам. Отдельно: Assistant (вопросы из UI с выделением)
  и Unknown Map Analyst (гипотезы ролей кандидата, confidence ≤ 0.8 и требует подтверждения человеком).
* Правила: без валидного evidence — отклонено; частично невалидные ссылки — confidence ×0.8; ответ ассистента без
  evidence — confidence ≤ 0.2; противоречие верификатора — REVIEW и ×0.4; итог проходит `GuardSafe`.
* Кэш: каждое обращение сначала идёт в `ai_cache`; повторный анализ тех же входов не тратит токены.

## 8. Правила confidence

* **Доступность данных** (`DataAvailability`): BIN 0.30, +VIN 0.08, +stock 0.07, +определения карт 0.20,
  +подтверждённое пользователем железо 0.12, +диагностические логи 0.15; итог ограничен [0.2, 0.9]. Это **cap** для
  всех оценок симуляции и риска: без логов confidence не может быть «высокой».
* Определения: DefinitionDb 0.9, XDF 0.85, сигнатурный скан — по гипотезам классификатора (< 0.9).
* Уровни: <0.35 Low, <0.5 Low-Medium, <0.65 Medium, <0.8 Medium-High, иначе High.
* Ансамбль моделей и консенсус AI понижают confidence при разногласии, а не усредняют его.

## 9. Производительность

Синтетический образ 512 КБ, BenchmarkDotNet (short job, Linux x64, .NET 10), см. `docs/benchmarks.md`:

| Операция | Время |
|---|---|
| Полный анализ (stock vs Stage 1, все шаги) | ≈ 18 мс |
| Сканер Bosch-карт по 512 КБ | ≈ 2 мс |
| Diff байт + карт | ≈ 60 мкс |
| Сетка рабочих точек + сценарии | ≈ 0.55 мс |
| Одна рабочая точка | ≈ 3 мкс |
| Билинейная интерполяция | ≈ 15 нс, без аллокаций |

Hot paths работают по `ReadOnlySpan<byte>`; образ неизменяемый и разделяется между шагами без копий.

## 10. Frontend

Next.js 16 (static export) + TypeScript + Tailwind v4 (CSS-токены, тёмная/светлая тема) + TanStack Query/Virtual +
ECharts (lazy, 3D через echarts-gl) + React Flow + cmdk + zustand. Собирается в `src/ECUStudio.Api/wwwroot` и
раздаётся тем же хостом (сервер и desktop). Структура: `components/ui` (design system: badges источника/confidence/
severity, EstimateValue, LoadBar), `components/layout` (top bar, nav, AI-панель, палитра Ctrl+K), `features/<раздел>`
— по странице на раздел, тонкие `app/workspace/<section>/page.tsx`.

## 11. Trade-offs

* **Документное хранение отчёта** вместо нормализации — см. §5.
* **Plain SQL миграции** вместо EF Migrations — читаемость и один механизм на два провайдера ценой ручного SQL.
* **Стационарная физика** вместо 1D/0D-динамики — достаточно для оценки запасов при WOT, не моделирует переходные
  процессы (спул турбины, overshoot). Поэтому overshoot boost не оценивается, только уставки.
* **Синтетические демо-бинарники** — реальных калибровок в репозитории нет (лицензии); формы карт правдоподобны,
  но это не реальная прошивка и всё в демо помечено как synthetic.
* **AI-результат хранится в памяти процесса** (кэш ответов агентов — в БД), т.е. после рестарта разбор
  пересобирается из кэша без затрат токенов, но требует повторного запуска.

## 12. Ограничения текущей версии и roadmap

Сейчас:
* Контрольные суммы EDC16U34 не реализованы (статус `NotImplemented`) — после правки файл нельзя считать готовым к записи.
* Импорт A2L / DAMOS / OLS — заглушки с понятной ошибкой; работают XDF и нативный JSON.
* Раздел Logs (диагностические логи) — не реализован, UI честно об этом говорит.
* Большинство пределов компонентов в каталоге `UNKNOWN` (нет опубликованных данных) — это by design.
* Desktop собирается на Linux, но не проверен на Windows.

Дальше:
1. Чексуммы EDC16U34 (и запись файла только при `Valid`).
2. Импорт диагностических логов (VCDS / CSV) → калибровка модели по измерениям, рост confidence cap.
3. Плагины EDC15/EDC17/MED17 (интерфейс `IEcuPlugin` уже отделён от ядра).
4. Импорт A2L/DAMOS.
5. Проекции отчётов в БД для поиска по парку прошивок.
