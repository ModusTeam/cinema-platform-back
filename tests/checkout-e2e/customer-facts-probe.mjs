import assert from 'node:assert/strict';
import { createHash, createHmac, randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { isAbsolute, resolve } from 'node:path';
import { existsSync, writeFileSync } from 'node:fs';

function required(name) {
  assert.ok(process.env[name], `${name} missing; use the disposable checkout runner`);
  return process.env[name];
}
const require = createRequire(resolve(required('LOYALTY_REPO'), 'package.json'));
const { Pool } = require('pg');
const { connect } = require('amqplib');
for (const name of ['E2E_MONOLITH_DATABASE_URL', 'E2E_LOYALTY_DATABASE_URL', 'E2E_MONOLITH_URL', 'E2E_RABBIT_URL', 'E2E_RABBIT_HTTP'])
  assert.equal(new URL(required(name)).hostname, '127.0.0.1', `${name} must be loopback`);
const composeArgs = JSON.parse(required('E2E_COMPOSE_ARGS'));
assert.ok(Array.isArray(composeArgs) && composeArgs.every(value => typeof value === 'string'));
const dockerExecutable = required('E2E_DOCKER_EXECUTABLE');
assert.ok(isAbsolute(dockerExecutable) && existsSync(dockerExecutable), 'Use an absolute Docker CLI executable');
const backend = new Pool({ connectionString: required('E2E_MONOLITH_DATABASE_URL') });
const loyalty = new Pool({ connectionString: required('E2E_LOYALTY_DATABASE_URL') });
const queue = 'loyalty_ticket_purchased';
const evidence = [];
let connection;
let channel;

function compose(...args) {
  const result = spawnSync(dockerExecutable, [...composeArgs, ...args], { stdio: 'inherit', timeout: 90000 });
  if (result.error) throw result.error;
  assert.equal(result.status, 0, `Compose ${args.join(' ')} failed`);
  // Docker assigns new random published ports when a stopped container starts.
  // Keep loopback isolation and rediscover endpoints instead of fixing ports.
  if (args[0] === 'start') {
    const bindings = [
      ['cinema-rabbitmq', 5672, 'E2E_RABBIT_URL'],
      ['cinema-rabbitmq', 15672, 'E2E_RABBIT_HTTP'],
      ['monolith-api', 8080, 'E2E_MONOLITH_URL'],
      ['loyalty-service', 3000, 'E2E_LOYALTY_URL'],
    ];
    for (const [service, port, name] of bindings) {
      if (!args.includes(service)) continue;
      const mapped = spawnSync(dockerExecutable, [...composeArgs, 'port', service, String(port)], { encoding: 'utf8', timeout: 20000 });
      assert.equal(mapped.status, 0);
      const address = mapped.stdout.trim();
      assert.match(address, /^127\.0\.0\.1:\d+$/);
      const endpoint = new URL(required(name));
      endpoint.host = address;
      process.env[name] = endpoint.toString().replace(/\/$/, '');
    }
  }
}
async function until(check, label, timeout = 45000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    if (await check()) return;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Timed out: ${label}`);
}
async function apiReady() {
  await until(async () => {
    try { return (await fetch(required('E2E_MONOLITH_URL') + '/swagger/v1/swagger.json', { signal: AbortSignal.timeout(1500) })).ok; }
    catch { return false; }
  }, 'API ready', 60000);
}
function token(userId) {
  const jwt = JSON.parse(required('E2E_TEST_JWT_SETTINGS'));
  const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
  const input = encode({ alg: 'HS256', typ: 'JWT' }) + '.' + encode({ sub: userId, iss: jwt.issuer, aud: jwt.audience, exp: Math.floor(Date.now() / 1000) + 900 });
  return input + '.' + createHmac('sha256', jwt.secret).update(input).digest('base64url');
}
async function request(path, method, data, userId) {
  const response = await fetch(required('E2E_MONOLITH_URL') + path, {
    method, headers: { 'Content-Type': 'application/json', ...(userId ? { Authorization: 'Bearer ' + token(userId) } : {}) },
    body: JSON.stringify(data), signal: AbortSignal.timeout(20000),
  });
  assert.equal(response.status, method === 'PUT' ? 204 : 200, `${method} ${path}: ${await response.clone().text()}`);
  return response.status === 204 ? undefined : response.json();
}
async function register() {
  const result = await request('/api/auth/register', 'POST', {
    email: `facts-${randomUUID()}@example.test`, password: 'Proof1!password', firstName: 'Facts', lastName: 'Fixture',
  });
  assert.equal(typeof result.userId, 'string');
  return result.userId;
}
async function facts(userId) {
  return (await backend.query('SELECT event_id, revision, snapshot FROM customer_fact_revisions WHERE user_id=$1 ORDER BY revision', [userId]))
    .rows.map(row => ({ ...row, envelope: JSON.parse(row.snapshot) }));
}
async function projection(userId, revision) {
  await until(async () => (await loyalty.query('SELECT applied_revision FROM customer_facts_projections WHERE user_id=$1', [userId])).rows[0]?.applied_revision === revision,
    `customer projection revision ${revision}`);
  const source = (await facts(userId)).find(fact => fact.revision === revision);
  assert.ok(source);
  const row = (await loyalty.query('SELECT *,date_of_birth::text AS calendar_dob FROM customer_facts_projections WHERE user_id=$1', [userId])).rows[0];
  const expected = source.envelope.data;
  assert.equal(row.source_event_id, expected.eventId);
  assert.equal(row.registered_at_utc?.toISOString() ?? null, expected.registeredAtUtc);
  assert.equal(row.dob_first_set_at_utc?.toISOString() ?? null, expected.dobFirstSetAtUtc);
  assert.equal(row.occurred_at_utc.toISOString(), expected.occurredAtUtc);
  assert.equal(row.calendar_dob, expected.dateOfBirth);
}
async function finances(userId) {
  const profile = (await loyalty.query('SELECT balance,lifetime_points,year_points,year_visits,tier,gold_upgrade_used_month FROM loyalty_profiles WHERE user_id=$1', [userId])).rows[0];
  const counts = (await loyalty.query(`SELECT
    (SELECT count(*)::int FROM points_transactions WHERE user_id=$1) AS ledger,
    (SELECT count(*)::int FROM user_bonus_grants WHERE user_id=$1) AS grants,
    (SELECT count(*)::int FROM user_achievements WHERE user_id=$1) AS achievements,
    (SELECT count(*)::int FROM achievement_dispatch_intents WHERE user_id=$1) AS intents`, [userId])).rows[0];
  return { profile, ...counts };
}
function zeroFinancials(value) {
  assert.deepEqual(value, { profile: { balance: 0, lifetime_points: 0, year_points: 0, year_visits: 0, tier: 'BRONZE', gold_upgrade_used_month: null }, ledger: 0, grants: 0, achievements: 0, intents: 0 });
}
async function broker(path) {
  const response = await fetch(required('E2E_RABBIT_HTTP') + '/api/' + path, {
    headers: { Authorization: 'Basic ' + Buffer.from('guest:guest').toString('base64') }, signal: AbortSignal.timeout(3000),
  });
  assert.equal(response.status, 200);
  return response.json();
}
async function openChannel() {
  connection = await connect(required('E2E_RABBIT_URL'));
  channel = await connection.createConfirmChannel();
}
async function closeChannel() {
  if (channel) await channel.close();
  if (connection) await connection.close();
  channel = connection = undefined;
}
async function publish(envelope, suffix = '\n') {
  const body = Buffer.from(JSON.stringify(envelope) + suffix);
  channel.sendToQueue(queue, body, { persistent: true, contentType: 'application/json', messageId: envelope.data.eventId ?? randomUUID() });
  await channel.waitForConfirms();
  return createHash('sha256').update(queue).update('\0').update(body).digest('hex');
}
async function completed(deliveryId) {
  await until(async () => !!(await loyalty.query('SELECT completed_at FROM inbound_deliveries WHERE id=$1', [deliveryId])).rows[0]?.completed_at, 'durable inbound completion');
}

try {
  await apiReady();
  // Seed only the intended role fixture, not a fabricated registered user.
  await backend.query('INSERT INTO "AspNetRoles" (id,name,normalized_name,concurrency_stamp) VALUES ($1,\'User\',\'USER\',$2) ON CONFLICT (normalized_name) DO NOTHING', [randomUUID(), randomUUID()]);
  const normalDob = new Date().getUTCMonth() === 0 && new Date().getUTCDate() === 1 ? '1990-02-01' : '1990-01-01';
  for (const path of ['/api/account/profile/date-of-birth', '/api/users/me/date-of-birth', '/api/account/profile']) {
    const userId = await register();
    await projection(userId, 1); zeroFinancials(await finances(userId));
    await request(path, path.endsWith('/profile') ? 'PUT' : 'PATCH', { firstName: 'Facts', lastName: 'Fixture', dateOfBirth: normalDob }, userId);
    await projection(userId, 2);
    await until(async () => (await loyalty.query('SELECT birthday_date FROM loyalty_profiles WHERE user_id=$1', [userId])).rows[0]?.birthday_date, 'legacy DOB delivery');
    zeroFinancials(await finances(userId));
    const original = (await facts(userId))[1];
    await openChannel();
    await completed(await publish(original.envelope));
    await closeChannel();
    zeroFinancials(await finances(userId));
    assert.equal((await facts(userId)).length, 2);
    evidence.push({ scenario: 'HTTP registration and DOB', path, userId, eventId: original.event_id, revision: 2 });
  }

  // Keep the API running while RabbitMQ is down: request completion must depend
  // on DB staging, not broker availability. Then restart the actual delivery host.
  compose('stop', 'cinema-rabbitmq');
  const delayedUser = await register();
  await request('/api/account/profile/date-of-birth', 'PATCH', { dateOfBirth: normalDob }, delayedUser);
  const delayed = await facts(delayedUser);
  assert.equal(delayed.length, 2);
  const persisted = (await backend.query('SELECT body,message_id FROM outbox_message WHERE message_id=ANY($1::uuid[]) ORDER BY message_id', [delayed.map(fact => fact.event_id)])).rows;
  assert.equal(persisted.length, 2, 'both committed source facts remain in Bus Outbox');
  for (const record of persisted) assert.deepEqual(JSON.parse(record.body), delayed.find(fact => fact.event_id === record.message_id).envelope);
  compose('stop', 'monolith-api');
  compose('start', 'cinema-rabbitmq');
  await until(async () => { try { return (await broker('overview')).rabbitmq_version; } catch { return false; } }, 'broker restored');
  await openChannel();
  await completed(await publish(delayed[1].envelope));
  assert.equal((await loyalty.query('SELECT state FROM customer_fact_receipts WHERE event_id=$1', [delayed[1].event_id])).rows[0]?.state, 'PENDING');
  zeroFinancials(await finances(delayedUser));
  await completed(await publish(delayed[0].envelope));
  await projection(delayedUser, 2);
  assert.equal((await loyalty.query('SELECT birthday_date FROM loyalty_profiles WHERE user_id=$1', [delayedUser])).rows[0].birthday_date, null, 'passive facts cannot overwrite legacy DOB');
  zeroFinancials(await finances(delayedUser));
  await closeChannel();
  compose('start', 'monolith-api');
  await apiReady();
  await until(async () => (await backend.query('SELECT count(*)::int AS count FROM outbox_message WHERE message_id=ANY($1::uuid[])', [delayed.map(fact => fact.event_id)])).rows[0].count === 0, 'original serialized outbox delivery and cleanup');
  await until(async () => (await loyalty.query('SELECT birthday_date FROM loyalty_profiles WHERE user_id=$1', [delayedUser])).rows[0]?.birthday_date, 'legacy DOB after broker recovery');
  assert.equal((await loyalty.query('SELECT count(*)::int AS count FROM customer_fact_receipts WHERE user_id=$1', [delayedUser])).rows[0].count, 2);
  zeroFinancials(await finances(delayedUser));
  evidence.push({ scenario: 'broker outage + actual delivery host restart + produced revision 2 before 1', userId: delayedUser, eventIds: delayed.map(fact => fact.event_id) });

  // A changed payload under the same event identity must quarantine, and must
  // not prevent an unrelated real registration from progressing.
  await openChannel();
  const conflict = structuredClone(delayed[1].envelope);
  conflict.data.dateOfBirth = '1989-03-02';
  const conflictDelivery = await publish(conflict);
  await until(async () => !!(await loyalty.query('SELECT quarantined_at FROM inbound_deliveries WHERE id=$1', [conflictDelivery])).rows[0]?.quarantined_at, 'conflicting event quarantine');
  await closeChannel();
  const otherUser = await register(); await projection(otherUser, 1); zeroFinancials(await finances(otherUser));
  assert.equal((await loyalty.query('SELECT applied_revision FROM customer_facts_projections WHERE user_id=$1', [delayedUser])).rows[0].applied_revision, 2);

  // Restage retained source history after original transport rows were removed.
  await until(async () => { const state = await broker('queues/%2F/' + queue); return state.messages === 0; }, 'inbound queue drained before inspection');
  compose('stop', 'loyalty-service');
  compose('run', '--rm', '--no-deps', 'monolith-migrate', '--restage-customer-fact', delayed[1].event_id);
  await openChannel();
  let replay;
  await until(async () => { replay = await channel.get(queue, { noAck: false }); return !!replay; }, 'restaged transport delivery');
  assert.deepEqual(JSON.parse(replay.content.toString()), delayed[1].envelope);
  assert.equal(replay.properties.messageId, delayed[1].event_id);
  channel.nack(replay, false, true);
  await closeChannel();
  compose('start', 'loyalty-service');
  await until(async () => { const state = await broker('queues/%2F/' + queue); return state.messages === 0; }, 'restaged fact ACK');
  assert.deepEqual((await facts(delayedUser)).map(fact => fact.snapshot), delayed.map(fact => fact.snapshot));
  zeroFinancials(await finances(delayedUser));

  // A real legacy birthday grant remains exactly once after a schema-equivalent
  // legacy republish and a repeated original passive fact. This is manual
  // duplicate evidence, separate from the loyalty suite's lost-ACK redelivery.
  const birthdayUser = await register();
  const now = new Date();
  const birthday = `1992-${String(now.getUTCMonth() + 1).padStart(2, '0')}-${String(now.getUTCDate()).padStart(2, '0')}`;
  await request('/api/users/me/date-of-birth', 'PATCH', { dateOfBirth: birthday }, birthdayUser);
  await projection(birthdayUser, 2);
  await until(async () => (await finances(birthdayUser)).ledger === 1, 'legacy birthday grant');
  const birthdayBefore = await finances(birthdayUser);
  assert.equal(birthdayBefore.profile.balance, 100); assert.equal(birthdayBefore.grants, 1);
  const birthdayFact = (await facts(birthdayUser))[1];
  const legacy = { pattern: 'USER_DATE_OF_BIRTH_SET', data: { userId: birthdayUser, dateOfBirth: birthday, occurredAtUtc: birthdayFact.envelope.data.dobFirstSetAtUtc } };
  await openChannel();
  await completed(await publish(legacy));
  await completed(await publish(birthdayFact.envelope));
  await closeChannel();
  assert.deepEqual(await finances(birthdayUser), birthdayBefore);
  evidence.push({ scenario: 'stored-history replay, conflicting identity isolation, legacy birthday dedupe', eventId: birthdayFact.event_id, birthdayUser });

  const overview = await broker('overview');
  const inbound = await broker('queues/%2F/' + queue);
  evidence.push({ scenario: 'fixture broker facts', rabbitmqVersion: overview.rabbitmq_version, queueType: inbound.type,
    effectivePolicy: inbound.effective_policy_definition, policies: await broker('policies'),
    prefetch: inbound.consumer_details?.map(consumer => consumer.prefetch_count) });
  writeFileSync(resolve('.tools/e2e/customer-facts-evidence.json'), JSON.stringify(evidence, null, 2));
  console.log('PASS customer facts HTTP, source history, real RabbitMQ outage/restart, revision ordering, duplicates, restaging and unchanged financial effects');
} finally {
  await closeChannel();
  await backend.end(); await loyalty.end();
}
