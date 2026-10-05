# Evidencia e2e — cierre de la auditoría (specs 14 a 17)

**Fecha:** 2026-10-02
**Target:** Temporal de `ssy-yardflow` (`localhost:7233`), namespace observado `default`, namespace del
monitor `monitor`. Stack del monitor ya desplegado con el código de los specs 14 a 17.

## Verificado en vivo

| Hallazgo | Comprobación | Resultado |
| -------- | ------------ | --------- |
| A-4 | `dotnet test PatchMonitor.sln` completo en una sola corrida | 375/375, 0 fallos |
| A-3 / spec 17 | Logs del worker sin `TMPRL1100`/nondeterminism; `GET /patches` con 6 entities, `error: null` | OK (detalle en `spec-17-evidence.md`) |
| M-2 | 6 × `POST /health/workflow` seguidos | `200` en todos |
| M-3 | `GET /schedule`: intervalo `00:05:00`, `numActions` creciendo, `paused: false` | OK |
| M-4 | `docker inspect`: worker y API con `restart: unless-stopped` | OK |
| M-5 | `docker restart` del Temporal sin tocar worker ni API: `/health/workflow` y `/patches` responden `200` y el tick de las 20:50Z completa (`errors: []`) | OK, sin reiniciar el monitor |
| M-8 | `GET /runs` devuelve las corridas más recientes primero | OK |
| B-1 / B-5 | `GET /patches` responde con el campo `error` y sin degradación (6 patches) | OK |
| B-6 | `shadcn` en `devDependencies` de `web/package.json` | OK |
| B-7 | `id -u` en worker y API = `1654`, dashboard = `101` (ninguno root) | OK |

## Cubierto solo por tests (sin disparador natural en el cluster)

El cluster de yardflow no tenía ejecuciones recientes en la ventana de descubrimiento
(`patchesDiscovered: 0`), así que estos casos dependen de los tests del spec correspondiente:

- A-1 (fallo de webhook ⇒ reintento en el tick siguiente), A-2 (`ContinuedAsNew` no bloquea),
- M-6 (`Clean` conservado), M-7 (topes por defecto), M-9 (rotación de patches),
- B-2, B-3, B-4, B-8.

## Conclusión

Sin regresiones en vivo y suite verde. La auditoría queda cerrada, con **M-1 (token de la API)**
como límite conocido documentado en el README, por decisión explícita.

> **Actualización 2026-10-05:** M-6 y M-9 se verificaron después en vivo, ver
> `audit-m6-m9-evidence.md`.

## Ampliación: A-1 y A-2 en vivo

Worker desechable (`AuditE2EWorkflow`, patch `audit-e2e-v1`, cola `audit-e2e-queue`) más un webhook
local que se puede hacer fallar a demanda; monitor con `NOTIFIER_WEBHOOK_URL` apuntando a él.

| Hallazgo | Escenario | Resultado |
| -------- | --------- | --------- |
| A-1 | Webhook devolviendo `500` durante el tick que descubre el patch (revisión 1) | 3 intentos, `notificationsFailed: 1`, error registrado; la revisión no se reclamó |
| A-1 | Webhook restaurado, siguiente tick | La misma revisión se reenvía: `notificationsSent: 1`; el tercer tick no repite |
| A-2 | Fase 1: una ejecución con marker hace Continue-As-New y se cierra la cadena; luego fase 2 con una ejecución nueva abierta | `Blocked` con `blockingExecutionCount: 1` (solo la abierta; la run `ContinuedAsNew` con marker no cuenta) |
| A-2 | Se cierra la ejecución abierta | `Ready` (gate 2→3) con `blockingExecutionCount: 0` |

Sin errores de no determinismo en los logs. M-6, M-7, M-9, B-2, B-3, B-4 y B-8 siguen cubiertos
por tests. El patch de prueba `audit-e2e-v1` queda en el registro del cluster de yardflow hasta
que salga de la ventana de descubrimiento (7 días).
