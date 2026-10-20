import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';

function required(name) {
  assert.ok(process.env[name], name + ' is required by the disposable checkout runner');
  return process.env[name];
}
const require = createRequire(resolve(required('LOYALTY_REPO'), 'package.json'));
const { Pool } = require('pg');
const Redis = require('ioredis');
const databaseUrl = new URL(required('E2E_MONOLITH_DATABASE_URL'));
assert.equal(databaseUrl.hostname, '127.0.0.1');
assert.equal(databaseUrl.pathname, '/cinema_local_db');
const api = required('E2E_MONOLITH_URL');
assert.equal(new URL(api).hostname, '127.0.0.1');
const evidenceFile = required('E2E_CHECKOUT_EVIDENCE_FILE');
const evidence = JSON.parse(readFileSync(evidenceFile, 'utf8'));
const fixture = evidence.find(row => row.scenario === 'declined-gold');
const uncertain = evidence.find(row => row.scenario === 'unknown-payment');
assert.ok(fixture && uncertain, 'Strict loyalty checkout scenarios must finish first');
const { userId, sessionId } = fixture;
const seatId = fixture.order.seat_id;
const jwt = JSON.parse(required('E2E_TEST_JWT_SETTINGS'));
const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
const unsigned = encode({ alg: 'HS256', typ: 'JWT' }) + '.' + encode({
  sub: userId, iss: jwt.issuer, aud: jwt.audience, exp: Math.floor(Date.now() / 1000) + 300,
});
const token = unsigned + '.' + createHmac('sha256', jwt.secret).update(unsigned).digest('base64url');
const pool = new Pool({ connectionString: databaseUrl.toString() });
const redis = new Redis({ host: '127.0.0.1', port: Number(required('E2E_MONOLITH_REDIS_PORT')), password: 'redis_password' });
const seatUrl = api + '/api/sessions/' + sessionId + '/seats';

async function availability(id, available) {
  // Anonymous requests, identical URL, no cache-busting headers/query/flush.
  const response = await fetch(seatUrl, { signal: AbortSignal.timeout(5000) });
  assert.equal(response.status, 200);
  const body = await response.json();
  const seat = body.seats.find(item => item.id === id);
  assert.ok(seat);
  assert.equal(seat.isAvailable, available);
  assert.equal(seat.isSold, !available);
  assert.equal(seat.isLocked, false);
  return { seat, age: response.headers.get('age') };
}
async function post(path, body) {
  return fetch(api + path, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + token },
    body: JSON.stringify(body), signal: AbortSignal.timeout(20000),
  });
}
async function lock() {
  await redis.set('lock:session:{' + sessionId + '}:' + seatId, userId, 'EX', 600);
}
async function reserve() {
  await lock();
  const response = await post('/api/orders/reservations', { sessionId, seatIds: [seatId] });
  assert.equal(response.status, 200);
  const body = await response.json();
  assert.equal(body.status, 'Pending');
  assert.equal(typeof body.id, 'string');
  return body.id;
}
async function state(orderId, orderStatus, ticketStatus) {
  const result = await pool.query('SELECT o.status, t.ticket_status FROM orders o JOIN tickets t ON t.order_id=o.id WHERE o.id=$1', [orderId]);
  assert.deepEqual(result.rows, [{ status: orderStatus, ticket_status: ticketStatus }]);
}
try {
  const before = await availability(seatId, true);
  const releasedOrder = await reserve();
  await state(releasedOrder, 0, 3); // Pending / Reserved
  const reserved = await availability(seatId, false);
  await lock();
  const duplicate = await post('/api/orders/reservations', { sessionId, seatIds: [seatId] });
  assert.equal(duplicate.ok, false);
  assert.equal((await duplicate.json()).code, 'Order.SeatsSold');
  const cancelled = await post('/api/orders/' + releasedOrder + '/cancel', {});
  assert.equal(cancelled.status, 204);
  await state(releasedOrder, 3, 4); // Cancelled / Released
  const released = await availability(seatId, true);

  const paidOrder = await reserve();
  const payment = await post('/api/orders/' + paidOrder + '/checkout', {
    paymentToken: 'local-success-token', useLoyaltyPoints: false, applyGoldUpgrade: false,
  });
  assert.equal(payment.status, 200);
  assert.equal(await payment.json(), paidOrder);
  await state(paidOrder, 1, 0); // Paid / Valid
  const paid = await availability(seatId, false);

  await state(uncertain.orderId, 5, 3); // PaymentOutcomeUnknown / Reserved
  const uncertainSeat = await pool.query('SELECT seat_id FROM tickets WHERE order_id=$1', [uncertain.orderId]);
  const unknown = await availability(uncertainSeat.rows[0].seat_id, false);
  evidence.push({ scenario: 'anonymous-seat-transitions', seatUrl, before, reserved, released, paid, unknown, releasedOrder, paidOrder });
  writeFileSync(evidenceFile, JSON.stringify(evidence, null, 2));
  console.log('PASS anonymous same-URL seat transitions: available -> reserved -> released; paid and unknown remain unavailable; PostgreSQL states and resale rejection match');
} finally {
  await redis.quit();
  await pool.end();
}
