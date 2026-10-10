// Нагрузочный сценарий Motiva (T09): открытая модель 100 req/s.
// Состав: 50% каталог, 25% личный кошелёк, 10% live leaderboard,
//         10% новые progress events, 5% новые spends по 1 единице.
// Подготовка данных — вне измерения; здесь задаются seed-идентификаторы.
import http from 'k6/http';
import { check, sleep } from 'k6';

const API = __ENV.API || 'http://localhost:8080';
const TOKEN = __ENV.TOKEN || '';               // employee token
const SRC_TOKEN = __ENV.SRC_TOKEN || '';       // progress-source token
const EMPLOYEE_ID = Number(__ENV.EMPLOYEE_ID || 1);
const TASK_ID = Number(__ENV.TASK_ID || 1);
const CAMPAIGN_ID = Number(__ENV.CAMPAIGN_ID || 1);
const RESOURCE_ID = Number(__ENV.RESOURCE_ID || 1);
const PURCHASE_SYSTEM_ID = Number(__ENV.PURCHASE_SYSTEM_ID || 1);
const CHALLENGE_ID = Number(__ENV.CHALLENGE_ID || 1);

const AUTH = { Authorization: `Bearer ${TOKEN}` };
const SRC_AUTH = { Authorization: `Bearer ${SRC_TOKEN}` };

export const options = {
  // открытая модель: 100 RPS, прогрев 60с, измерение 300с
  scenarios: {
    open: {
      executor: 'constant-arrival-rate',
      rate: 100,
      timeUnit: '1s',
      duration: '300s',
      preAllocatedVUs: 100,
      maxVUs: 300,
    },
  },
  thresholds: {
    http_req_duration: ['p(95)<200', 'p(99)<1000'],
    http_req_failed: ['rate<0.005'],
  },
};

let spendSeq = 0;

export default function () {
  const r = Math.random();
  if (r < 0.50) {
    // каталог
    const res = http.get(`${API}/api/v1/me/catalog`, { headers: AUTH });
    check(res, { 'catalog 200': (x) => x.status === 200 });
  } else if (r < 0.75) {
    // личный кошелёк
    const res = http.get(`${API}/api/v1/wallet`, { headers: AUTH });
    check(res, { 'wallet 200': (x) => x.status === 200 });
  } else if (r < 0.85) {
    // live leaderboard
    const res = http.get(`${API}/api/v1/challenges/${CHALLENGE_ID}/leaderboard`, { headers: AUTH });
    check(res, { 'lb 200': (x) => x.status === 200 });
  } else if (r < 0.95) {
    // новый progress event
    const res = http.post(
      `${API}/internal/v1/events`,
      JSON.stringify({ employeeId: EMPLOYEE_ID, taskId: TASK_ID, increase: 1, sourceEventNo: `ev-${__VU}-${__ITER}` }),
      { headers: { ...SRC_AUTH, 'Content-Type': 'application/json' } },
    );
    check(res, { 'event 201': (x) => x.status === 201 });
  } else {
    // новый spend по 1 единице
    const n = spendSeq++;
    const res = http.post(
      `${API}/api/v1/spends`,
      JSON.stringify({ resourceId: RESOURCE_ID, amount: 1, purchaseSystemId: PURCHASE_SYSTEM_ID, basis: 'load' }),
      { headers: { ...AUTH, 'Content-Type': 'application/json', 'Idempotency-Key': `sp-${__VU}-${n}` } },
    );
    check(res, { 'spend 201': (x) => x.status === 201 });
  }
}