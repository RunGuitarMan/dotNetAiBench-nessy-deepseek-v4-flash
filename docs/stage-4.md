# stage-4 — Motiva: реализация и поставка

Серия 1 (первая сдача). Реализован стек T01–T10, вертикаль S1 и экономические сценарии.
Статус по каждому пункту честный; неисполненное помечено «не проверено».

## Что сделано (B/T-пункты, код и тесты)

| Область | Код | Тесты |
|---|---|---|
| T01 стек, проекты, миграции PG17 | `src/*`, `global.json`, `docker-compose.yml`, `Migrations/*` | `Motiva.Tests.Integration` (миграция на реальном PG17) |
| T02 структура/границы | `src/Motiva.{Domain,Application,Infrastructure,Api,Worker}` | `Motiva.Tests.Architecture` (4) |
| T03 JWT (HS256, claims, bootstrap) | `src/Motiva.Api/Program.cs`, `ActorFrom`, `/internal/v1/bootstrap` | `Motiva.Tests.Integration` (подписанные JWT) |
| T04 REST/контракт (основной состав) | `Program.cs`, `Dtos.cs`, ProblemDetails+`code`+`traceId`, ETag/If-Match, `OperationOutcome` | `Motiva.Tests.Integration` |
| B01–B08 сотрудники/ресурсы/кампании/стримы/задания/вехи/челленджи | `UseCases/BootstrapAndEmployees.cs`, `Campaigns.cs`, `StreamTasks.cs`, `ResourcesIntegrations.cs` | unit+integration |
| B09 публикация/редактирование | `Campaigns.cs` (PublishAsync, поля до/после) | integration |
| B12–B17 прогресс/завершение/повтор события | `UseCases/Economy.cs` (TransmitProgress), `ICompanyCalendar` | unit (календарь), integration (идемпотентность) |
| B18–B21 бюджеты/выделение/ручное начисление/награда | `Economy.cs` (AllocateBudget, ManualAccrual, GrantReward) | integration (E01, недостаток→declined) |
| B22–B28 кошелёк/списание/отмена/возврат | `Economy.cs` (Spend, CancelAccrual, RefundSpend), `WalletReads.cs`, одна успешная отмена | integration (E05, отмена+409) |
| B29–B33 вехи/достижения/рейтинг/финализация | `Economy.cs`, `LeaderboardStatements.cs`, worker | unit+integration |
| B34–B37 выписки (заказ/удаление/ссылка) | `LeaderboardStatements.cs`, `StatementGenerator.cs` | «не проверено» E2E (см. ниже) |
| T05 числа/время/бессрочные повторы | `DomainRules.cs`, `OperationRequest` (operation), `ProgressEvent` (event) | unit |
| T07 CSV-генератор, снимок | `Infrastructure/StatementGenerator.cs`, `CsvStatementWriter` | «не проверено» E2E |
| T08 тесты | — | unit 21, architecture 4, integration 5 — зелёные |
| T09 нагрузка | `load/motiva.js` (сценарий) | «не проверено» (k6 не запускался) |
| T10 поставка | `README.md`, `docker-compose.yml`, `.env.example`, `examples/requests.sh` | — |

## Команды из чистого checkout и фактические результаты

| Команда | Exit | Результат |
|---|---|---|
| `dotnet restore Motiva.slnx` | 0 | пакеты восстановлены |
| `dotnet build Motiva.slnx` | 0 | сборка успешна, без CS-предупреждений |
| `dotnet test tests/Motiva.Tests.Unit` | 0 | 21/21 passed |
| `dotnet test tests/Motiva.Tests.Architecture` | 0 | 4/4 passed |
| `dotnet test tests/Motiva.Tests.Integration` | 0 | 5/5 passed (PG17+Valkey+MinIO) |
| `dotnet ef database update` (на PG17) | 0 | миграция применена; schema проверена |
| `k6 run load/motiva.js` | — | **не проверено** (нагрузочный запуск не выполнялся) |

Интеграционные тесты сами поднимают PostgreSQL 17 / Valkey 9 / MinIO (Testcontainers), применяют миграции и
выполняют HTTP-сценарии с подписанными JWT (E01 два ресурса, идемпотентность повтора, недостаток бюджета,
E05 конкурентные траты, ручное начисление+отмена+одна успешная отмена).

## Известные отклонения и ограничения

- **Конкурентная защита** реализована изоляцией `Serializable` + retry в `IUnitOfWork.ExecuteSerializableAsync`
  и уникальными индексами; явные `SELECT ... FOR UPDATE` по единому порядку (stage-2 §4.7) не реализованы.
  Это подход, а не ослабление бизнес-правил; часть гонок покрыта тестами (E05), часть — не покрыта.
- **Кеш каталога/leaderboard (T06)** подключён (`ICache`/`ValkeyCache` с write-if-asOf-newer), но
  эндпоинты каталога и рейтинга читают PostgreSQL напрямую; кеширование в read-пути не задействовано.
  Свежесть/`asOf`/fallback — не проверены на runtime.
- **Выписки (B36/B37/T07)** — генератор и worker реализованы (снимок, CSV, S3 put-if-absent, CAS Ready,
  cleanup), но **E2E не прогонялся**: worker не запускался вместе с API на стенде. Готовность ссылки/файла —
  «не проверено».
- **Челлендж/финализация** — логика реализована, гонка на границе интервала (FT-149) и публикация
  финального рейтинга в кеш — не проверены E2E.
- **`GET /campaigns`** и **`GET /campaigns/{id}/budgets`** — заглушки (возвращают пустые страницы).
- **Readiness/liveness**, структурированные логи и метрики (T06 §6.4) — не реализованы.
- **Transport-идемпотентность** (T05, 24 ч) реализована частично (statements/download-links), матрица
  HT-220…232 не покрыта.
- **Нагрузка (T09)** — сценарий `load/motiva.js` подготовлен, запуск и метрики **не выполнены**.

Уязвимые transitive-зависимости (`System.Security.Cryptography.Xml` 9.0.0 из Minio) закрыты явной ссылкой 9.0.1;
остаются NU1903 от test-only зависимостей Testcontainers (SSH.NET).

## Входы этапа не менялись
Обнаруженных противоречий в ранее утверждённых ожиданиях/архитектуре нет; входные документы не переписывались.
Ограничения по FOR UPDATE и кешу — осознанные решения реализации, заявленные выше как отклонения, а не как правка требований.