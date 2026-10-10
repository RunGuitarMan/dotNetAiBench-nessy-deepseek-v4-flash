#!/usr/bin/env bash
# Примеры запросов Motiva: Employee / Admin / Integration (T10).
# Токены генерируются auth/jwt.py (см. README). Заполните TOKEN/API перед запуском.
set -euo pipefail
API="${API:-http://localhost:8080}"
TOKEN="${TOKEN:-$(cat auth/.local/tokens/employee123.jwt 2>/dev/null || echo '')}"
ADMIN_TOKEN="${ADMIN_TOKEN:-$(cat auth/.local/tokens/admin1.jwt 2>/dev/null || echo '')}"
SRC_TOKEN="${SRC_TOKEN:-$(cat auth/.local/tokens/progress-source.jwt 2>/dev/null || echo '')}"
AUTH=(-H "Authorization: Bearer $TOKEN")
ADMIN=(-H "Authorization: Bearer $ADMIN_TOKEN")

echo "== Employee: мой каталог =="
curl -s "${AUTH[@]}" "$API/api/v1/me/catalog"

echo "== Employee: мой кошелёк =="
curl -s "${AUTH[@]}" "$API/api/v1/wallet"

echo "== Employee: мои движения =="
curl -s "${AUTH[@]}" "$API/api/v1/wallet/movements"

echo "== Admin: ресурсы =="
curl -s "${ADMIN[@]}" "$API/api/v1/resources"

echo "== Admin: создать ресурс =="
curl -s "${ADMIN[@]}" -X POST -H "Content-Type: application/json" -H "Idempotency-Key: rsrc-demo-1" \
  -d '{"code":"STAR","name":"Звезда"}' "$API/api/v1/resources"

echo "== Admin: сотрудники =="
curl -s "${ADMIN[@]}" "$API/api/v1/employees"

echo "== Integration: передать прогресс (сервис progress-source) =="
# employeeId/taskId — фактически созданные ID; замените на реальные.
curl -s -H "Authorization: Bearer $SRC_TOKEN" -X POST -H "Content-Type: application/json" \
  -d '{"employeeId":1,"taskId":1,"increase":3,"sourceEventNo":"ev-demo-1"}' \
  "$API/internal/v1/events"

echo "== Admin: аудит =="
curl -s "${ADMIN[@]}" "$API/api/v1/audit"