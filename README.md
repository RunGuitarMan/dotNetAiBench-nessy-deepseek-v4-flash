# Motiva

Платформа мотивационных кампаний: сотрудники выполняют задания, получают игровые ресурсы, достижения, участвуют в челленджах; системы-интеграции передают прогресс, начисляют и списывают ресурсы. Стек: **.NET 10 / ASP.NET Core, EF Core 10 + Npgsql, PostgreSQL 17, Valkey 9, MinIO (S3)**.

Документация: `docs/` (01_BUSINESS, 02_ENGINEERING, 03_AUTH, stage-1..3, openapi.yaml). Контракт API — `docs/openapi.yaml`.

## Структура
```
src/Motiva.Domain        чистые правила, календарь компании
src/Motiva.Application   use cases, авторизация, идемпотентность (порты)
src/Motiva.Infrastructure PostgreSQL (EF/Npgsql), Valkey, S3, генератор CSV, миграции
src/Motiva.Api           REST /api/v1 + /internal/v1, JWT-проверка, ProblemDetails
src/Motiva.Worker        фоновые циклы: выписки, финализация челленджей, cleanup S3
tests/                   Unit / Integration (PG17+Valkey+MinIO) / Architecture
```

## Запуск (из чистого checkout)

### 1. Инфраструктура
```bash
docker compose up -d postgres valkey minio
```
(PostgreSQL 17, Valkey 9, MinIO; Compose-образы зафиксированы).

### 2. Токены (подписанные тестовые JWT)
```bash
python auth/jwt.py prepare   # создаёт auth/.local/api.env и tokens/*.jwt
```
Скрипт вызывает `dotnet user-jwts`; ключ сохраняется только в `auth/.local/` (вне Git).

### 3. Миграции (на PostgreSQL 17, включая пустую БД)
```bash
export MOTIVA_PG="Host=localhost;Port=5432;Database=motiva;Username=motiva;Password=motiva"
dotnet ef database update --project src/Motiva.Infrastructure --startup-project src/Motiva.Infrastructure
```

### 4. API и Worker
```bash
set -a; source auth/.local/api.env; set +a
dotnet run --project src/Motiva.Api          # http://localhost:8080
dotnet run --project src/Motiva.Worker
```
API требует `Auth:Issuer/Audience/SigningKeyBase64` (из `api.env`). Примеры запросов — `examples/requests.sh`.

### 5. Bootstrap (доверенное создание компании и Admin)
```bash
TOKEN=$(cat auth/.local/tokens/admin1.jwt)
curl -H "Authorization: Bearer $TOKEN" -X POST -H "Content-Type: application/json" \
  -d '{"companyName":"Demo","adminMasterId":1}' http://localhost:8080/internal/v1/bootstrap
```

## Тесты
```bash
dotnet test tests/Motiva.Tests.Unit          # чистые правила, календарь
dotnet test tests/Motiva.Tests.Architecture  # границы проектов
dotnet test tests/Motiva.Tests.Integration   # PG17+Valkey+MinIO (Testcontainers), HTTP, JWT
```
Интеграционные тесты поднимают реальные контейнеры PostgreSQL 17 / Valkey 9 / MinIO, применяют миграции и проходят вертикаль S1, идемпотентность, E01/E02/E05, отмены.

## Нагрузка (T09)
```bash
# подготовьте данные (см. stage-3 §9), затем:
k6 run load/motiva.js  # открытая модель 100 RPS, 60с прогрев + 300с измерение
```
Состав 50/25/10/10/5% (каталог/кошелёк/leaderboard/events/spends). Цели и ограничения — в `docs/02_ENGINEERING.md` (T09).

## Безопасность
Секреты и подписанные ключи не коммитятся (`auth/.local/` в `.gitignore`). `.env.example` — без реального ключа. JWT проверяется по Base64-байтам ключа; в логи не попадают токены/ключи/signed-URL.