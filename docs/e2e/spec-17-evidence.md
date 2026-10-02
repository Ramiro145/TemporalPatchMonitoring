# Evidencia e2e — spec 17 (entities antiguas determinísticas)

**Fecha:** 2026-10-02
**Target:** Temporal de `ssy-yardflow` (persistencia Postgres, `localhost:7233`), namespace observado
`default`, namespace del monitor `monitor`. Overlay local `docker-compose.patchmonitor.yml` más un
override propio con `restart: unless-stopped`.

## Contexto

La prueba e2e de cierre de la auditoría mostró que el spec 14 (A-3) solo protegía a las entities
nuevas. Con 6 entities antiguas vivas (arrancadas antes del spec 14), bajar
`PATCH_STATE_CAN_THRESHOLD` de 500 a 20 y reiniciar el worker produjo:

- `[TMPRL1100] Nondeterminism error: Continue as new workflow machine does not handle this event:
  HistoryEvent(id: N, WorkflowExecutionUpdateAccepted)` en el replay de 3 de las 6 entities
  (las de más de 20 assessments).
- `GET /patches/{ns}/{type}/{patchId}` con `500` sin cuerpo.
- Un tick de `MonitorWorkflow` colgado más de 4 minutos.

## Verificación con el código del spec 17

1. **Reconstrucción** del worker, la API y el dashboard con el código nuevo.
2. **Estado inicial** (`temporal workflow query --type HasRecordedOptions`): el registry y la entity
   `gate-saga-compensacion-v1` ya tenían opciones; `gate-cierre-terminal-v1`,
   `patchmonitor-live-demo-v1`, `patchmonitor-test-v1`, `patchmonitor-test-v2` y
   `gondola-snapshot-por-senal-v1` devolvían `false` (entities antiguas).
3. **Migración automática.** Se lanzó una ejecución sintética de `_1007_RailGondolaArrivalWorkflow`
   (cola `railyard-queue-dev`) y un tick: el monitor evaluó `gondola-snapshot-por-senal-v1` y la
   entity pasó a `HasRecordedOptions == true`; su historia muestra la señal de migración y el
   `MarkerRecorded` de `Workflow.Patched`.
4. **Cambio de umbral.** Con `PATCH_STATE_CAN_THRESHOLD=20` en worker y API y reinicio:
   - 0 errores de no determinismo en los logs de worker y API.
   - `GET /patches`: las 6 entities leídas con `error: null` (incluidas las antiguas sin migrar,
     como `patchmonitor-test-v2`).
   - `GET /patches/default/_1001_TruckScrapPurchaseWorkflow/patchmonitor-test-v2` → `200`.
   - Ticks completados sin errores ni colgarse.
5. **Restauración** de la configuración original (intervalo de 5 min, sin umbral personalizado) y
   borrado de la ejecución sintética.

## Límite conocido

Un entity antiguo que ya no recibe assessments (su patch no tiene ejecuciones en la ventana de
descubrimiento) no se migra: no lo ve ningún proceso. No es un riesgo, porque con este spec ningún
workflow lee el entorno y su replay es el mismo con cualquier valor de `PATCH_STATE_*`; se migra
en cuanto el patch vuelva a evaluarse.

## Lo que no se probó en vivo

El fallo rápido (`QueryFailureGuard`) y el `503` de `GET`: provocar un replay roto real exigiría
reintroducir la lectura del entorno que este spec elimina. Están cubiertos por tests unitarios y de
`MonitorWorkflow` con un store que lanza `WorkflowQueryFailedException`.
