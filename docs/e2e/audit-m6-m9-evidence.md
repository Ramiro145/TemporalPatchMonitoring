# Evidencia e2e — M-6 y M-9 en vivo (re-verificación de la auditoría)

**Fecha:** 2026-10-05
**Target:** Temporal de `ssy-yardflow` (`localhost:7233`), namespace observado `default`, namespace del
monitor `monitor`. Stack del monitor con el código de los specs 14 a 17 (imágenes ya construidas).
Worker desechable `AuditM6Workflow` (patch `audit-m6-v1`, cola `audit-m6-queue`) que arma la historia
a demanda. Estos dos casos solo estaban cubiertos por tests (`audit-closure-evidence.md`).

## M-6 — `Clean` no retrocede sin evidencia nueva

Historia sembrada (orden de arranque): 3 ejecuciones con marker, 5 sin marker, 1 con marker
deprecado, espera mayor que `PHASE_CLEAN_GRACE_MINUTES=1`, 6 sin marker.

| Paso | Resultado |
| ---- | --------- |
| Tick 15:50 | `Clean` (fase 3) por inferencia: `sin marker desde 15:46:10Z; código limpio` |
| Ventana reducida a 15 ejecuciones (`DISCOVERY_MAX_EXECUTIONS=15`): los 3 markers viejos salen, `p` baja a 1/4 y `N` sube a 11 (> 6 vistas) | La inferencia pura volvería a `Deprecated` |
| Tick 16:00 | `Resolution.Reason = "se conserva Clean: sin markers nuevos desde 15:50:00Z"`, fase 3 |
| Ejecución nueva con marker (reintroducción) → tick 16:05 | Sale de `Clean`: fase 1, `faltan 10 ejecuciones sin marker para confirmar Clean (p=0.28, N=10, vistas=0)` |

Las dos mitades de la regla quedan comprobadas: conserva `Clean` sin hechos nuevos y lo suelta ante un
marker posterior a `LastChangedAt`.

## M-9 — rotación con `MONITOR_MAX_PATCHES_PER_RUN=2`

4 patches descubiertos (`audit-m6-v1`, `-a`, `-b`, `-c`):

| Tick | `patchesDiscovered` | `patchesAssessed` | `patchesSkipped` | Evaluados |
| ---- | ------------------- | ----------------- | ---------------- | --------- |
| 16:05 | 4 | 2 | 2 | `audit-m6-c`, `audit-m6-v1` |
| 16:10 | 4 | 2 | 2 | `audit-m6-a`, `audit-m6-b` |

Los patches saltados en un tick se evalúan en el siguiente: ninguno queda sin atender. Sin errores en
ningún tick.

## Notas de la prueba

- El listado de ejecuciones consulta primero `ExecutionStatus = 'Running'` y después la ventana por
  `StartTime`; los workflows cron de yardflow (`state-reconcile-global` y otros) comparten el cupo de
  `DISCOVERY_MAX_EXECUTIONS`. Un tope muy bajo se llena con ellos (el primer intento con 12 no aisló
  los markers viejos; con 15 sí).
- `phaseReason` de la entity no se actualiza cuando la fase no cambia; para ver el motivo de un tick
  hay que leer el resultado de `AssessPatch` en la historia del `MonitorWorkflow`
  (`temporal workflow show -n monitor -w patch-monitor-run-<ts>` y decodificar los payloads).

## Restauración

`DISCOVERY_MAX_EXECUTIONS=500` y `MONITOR_MAX_PATCHES_PER_RUN=50` restaurados (worker recreado con el
compose original); worker desechable detenido. Los patches `audit-m6-*` quedan en el registro del
cluster de yardflow hasta que salgan de la ventana de descubrimiento (7 días).

## No re-verificado en vivo (sin cambios)

M-7 (topes por defecto; el worker sigue logueando la advertencia cuando el overlay fija
`DISCOVERY_MAX_HISTORIES=200`), B-2, B-3, B-4, B-8, `QueryFailureGuard` y el `503` de `GET`: siguen
cubiertos por tests, por las razones de `audit-closure-evidence.md` y `spec-17-evidence.md`.
