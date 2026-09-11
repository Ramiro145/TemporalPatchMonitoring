# PatchMonitor — monitor del ciclo de vida de patches de Temporal

Un monitor genérico que se conecta a **cualquier namespace de Temporal**, descubre qué patches de
Workflow (`Workflow.Patched` / `Workflow.DeprecatePatch`) hay en juego, resuelve en qué fase está
cada uno y, cada 5 minutos, evalúa si ya es seguro avanzar a la fase siguiente. Cuando el veredicto
cambia, lo registra de forma durable y avisa una sola vez.

No toca el código del proyecto observado: se apunta a otro cluster con variables de entorno.

**¿Primera vez con el proyecto?** Empezá por la guía visual, que explica desde cero el problema,
las tres fases y el recorrido real paso a paso:
<https://claude.ai/code/artifact/1b237336-261e-4cca-9807-589e748b7dbb>

---

## El problema que resuelve

En Temporal, cambiar el código de un Workflow que tiene ejecuciones vivas las rompe por
**no-determinismo**: al reproducir su historia, el código nuevo emite comandos distintos a los que
quedaron grabados. El mecanismo oficial para evitarlo es el *patching*, que tiene un ciclo de vida
fijo de tres fases:

| Fase | Nombre | Código en el Workflow | Se puede avanzar cuando… |
| ---- | ------ | --------------------- | ------------------------ |
| 1 | Convivencia (`Coexistence`) | `if (Workflow.Patched("id")) { nuevo } else { viejo }` | no queda **ninguna ejecución pre-patch abierta** |
| 2 | Deprecación (`Deprecated`) | `Workflow.DeprecatePatch("id");` + paso nuevo incondicional | no queda **ninguna ejecución abierta que lleve el marker** |
| 3 | Código limpio (`Clean`) | el paso nuevo sin ninguna mención al patch | (fase final) |

Avanzar antes de tiempo produce `NonDeterminismError` en producción. Saber *cuándo* se puede avanzar
exige revisar a mano, en cada deploy, qué ejecuciones siguen abiertas y si su historia tiene el
marker del patch. PatchMonitor automatiza exactamente esa revisión.

## Qué hace y qué no hace

| Hace | No hace |
| ---- | ------- |
| Descubre los `patchId` activos en un namespace | Editar código fuente ni redesplegar (pasar de `Patched` a `DeprecatePatch` es una decisión humana) |
| Resuelve la fase actual de cada patch (1, 2 o 3) | Abrir PRs ni disparar CI |
| Evalúa el gate de salto cada 5 minutos con un **Temporal Schedule**, sin polling | Terminar, reintentar ni "arreglar" workflows del proyecto observado |
| Guarda el veredicto de forma durable y lo expone por HTTP | Tocar ninguna base de datos de negocio |
| Notifica **una vez por cada cambio real** de veredicto (log estructurado y/o webhook) | Decidir por su cuenta: el veredicto es un insumo para la persona que despliega |

---

## Requisitos

- **Docker Desktop** (o Docker Engine + Compose v2). Es lo único necesario para correrlo.
- **.NET 8 SDK**, solo si vas a compilar o correr los tests fuera de Docker.
- Un cluster de Temporal a observar (opcional para una primera prueba: por defecto el monitor se
  observa a sí mismo).

Stack: .NET 8, `Temporalio` 1.9.0, `temporalio/auto-setup:1.23.0`, `temporalio/ui:2.23.0`,
Postgres 15. **Sin SQL Server**: el estado del monitor vive en Temporal mismo.

## Levantarlo

Desde la carpeta `docker/`:

```powershell
docker compose build
docker compose up -d
docker compose ps          # los 5 servicios deben quedar arriba
```

| Servicio | Qué es | Puerto en el host |
| -------- | ------ | ----------------- |
| `temporal` | Cluster de Temporal **propio del monitor** (guarda su estado) | `7234` |
| `temporal-db` | Postgres del cluster propio | `5433` |
| `temporal-ui` | UI web de ese cluster | `8234` → <http://localhost:8234> |
| `patch-monitor-worker` | Worker: descubre, evalúa, persiste y notifica | — |
| `monitor-api` | API HTTP de consulta y control | `5100` → <http://localhost:5100/swagger> |

Los puertos están corridos (7234, 8234, 5433) a propósito, para poder correr en la misma máquina
que un proyecto que ya use 7233/8233/5432.

Al arrancar, el worker crea el Schedule `patch-monitor-schedule` (idempotente) y desde ahí corre
una pasada cada 5 minutos. Para comprobar que está vivo:

```powershell
curl http://localhost:5100/health
docker compose logs --tail=50 patch-monitor-worker
```

## Apuntarlo a tu proyecto

El monitor distingue dos clusters:

- `TEMPORAL_HOST` — el **suyo**, donde guarda su estado. No lo cambies.
- `TARGET_TEMPORAL_HOST` + `TARGET_TEMPORAL_NAMESPACE` — el **observado**, el de tu proyecto.

Para no modificar el compose base, creá un overlay (el repo trae uno de ejemplo,
`docker/docker-compose.e2e.yml`, que apunta al `ReleaseOrderDemo`):

```yaml
# docker/docker-compose.miproyecto.yml
services:
  patch-monitor-worker:
    extra_hosts:
      - "host.docker.internal:host-gateway"
    environment:
      - TARGET_TEMPORAL_HOST=host.docker.internal:7233   # o el host:puerto real de tu cluster
      - TARGET_TEMPORAL_NAMESPACE=mi-namespace
      - DISCOVERY_LOOKBACK_DAYS=3
  monitor-api:
    environment:
      - TARGET_TEMPORAL_HOST=host.docker.internal:7233
      - TARGET_TEMPORAL_NAMESPACE=mi-namespace
```

```powershell
docker compose -f docker-compose.yml -f docker-compose.miproyecto.yml up -d
```

`host.docker.internal` sirve cuando tu Temporal publica el puerto en la misma máquina. Si está en
otro servidor, poné su dirección directamente. **Si ya habías levantado el monitor antes**, borrá su
volumen (`docker compose down -v`) para que el Schedule se recree con la configuración nueva.

Antes de confiar en el resultado, dimensioná los topes de descubrimiento al volumen de tu
namespace: el monitor inspecciona **todos** los workflow types del namespace, no solo los que
tienen patches (ver [Límites conocidos](#límites-conocidos)).

---

## Cómo funciona

```text
 Temporal Schedule (cada 5 min)
            │
            ▼
   MonitorWorkflow ── una pasada efímera, sin bucles ni timers
     1. Descubrir   → lista ejecuciones del namespace observado y lee sus markers
     2. Resolver    → decide la fase actual de cada patch
     3. Evaluar     → aplica el gate de salto de esa fase
     4. Persistir   → manda el assessment al entity workflow del patch
     5. Notificar   → solo si la revisión del estado avanzó
            │
            ▼
 PatchStateWorkflow (uno por patch, nunca cierra)  +  PatchRegistryWorkflow (índice)
            ▲
            │  consultas / overrides / control del Schedule
        MonitorApi (HTTP :5100)
```

**1. Descubrimiento en dos niveles.** Lista las ejecuciones abiertas y las cerradas dentro de la
ventana `DISCOVERY_LOOKBACK_DAYS`. Nivel 1: lee el search attribute `TemporalChangeVersion` que
Temporal escribe al llamar `Patched`. Nivel 2: lee la Event History de cada ejecución buscando el
marker `core_patch`, que es lo único que distingue fase 1 de fase 2 (el flag `deprecated`).
Las ejecuciones sin marker se atribuyen como *pre-patch* a los patches de su mismo workflow type.

**2. Resolución de fase.** Con los markers encontrados:

- marker más reciente sin deprecar → **Coexistence**
- marker más reciente deprecado → **Deprecated**
- ninguna ejecución abierta con marker **y** una ejecución sin marker que arrancó más de
  `PHASE_CLEAN_GRACE_HOURS` después del último marker → **Clean**
- sin evidencia → **Unknown**

Un override manual vigente (ver API) gana siempre sobre la fase inferida.

**3. Gates de salto.** Son asimétricos a propósito:

| Gate | Bloquean | Resultado |
| ---- | -------- | --------- |
| Coexistence → Deprecated | ejecuciones **abiertas sin marker** (pre-patch) | `Blocked` con la lista, o `Ready` |
| Deprecated → Clean | ejecuciones **abiertas con marker** | `Blocked` con la lista, o `Ready` |

Si el descubrimiento se truncó por los topes, el gate responde `Inconclusive` en vez de un `Ready`
falso. Las ejecuciones cerradas nunca bloquean.

**4. Estado durable sin base de datos.** Cada patch tiene su propio entity workflow
(`PatchStateWorkflow`) que acumula los assessments, guarda el último veredicto y un historial de
cambios, y hace `Continue-As-New` al superar `PATCH_STATE_CAN_THRESHOLD`. Un índice singleton
(`PatchRegistryWorkflow`) permite listar todos los patches sin depender de Visibility.

**5. Notificación.** Solo cuando el estado del patch cambió (fase, resultado del gate o fase
siguiente). Una corrida sin cambios no avisa. Siempre se escribe una línea JSON en el log del
worker; si `NOTIFIER_WEBHOOK_URL` está definida, además se hace `POST` del mismo JSON.

---

## Usar la API

Swagger en <http://localhost:5100/swagger>.

| Método y ruta | Para qué |
| ------------- | -------- |
| `GET /health` | ¿Responde el cluster del monitor? |
| `GET /patches` | Todos los patches conocidos con su fase y gate |
| `GET /patches/{ns}/{type}/{patchId}` | Detalle: veredicto actual y anterior, bloqueantes, historial de cambios |
| `POST /patches/{ns}/{type}/{patchId}/override` | Forzar la fase de un patch (con vencimiento) |
| `DELETE /patches/{ns}/{type}/{patchId}/override` | Quitar el override y volver a la fase inferida |
| `GET /schedule` | Estado del Schedule: pausado, intervalo, última y próxima corrida |
| `POST /schedule/pause?note=...` | Pausar las corridas automáticas |
| `POST /schedule/unpause?note=...` | Reanudarlas |
| `POST /schedule/trigger` | Correr una pasada ya, sin esperar al próximo tick |

Las respuestas usan números para los enums:

| Campo | Valores |
| ----- | ------- |
| `phase`, `nextPhase` | `0` Unknown · `1` Coexistence · `2` Deprecated · `3` Clean |
| `outcome` | `0` Inconclusive · `1` Blocked · `2` Ready · `null` en fase final |
| `source` | `0` Inferred · `1` Override |

Ejemplo — revisar un patch después de forzar una pasada:

```powershell
curl -X POST http://localhost:5100/schedule/trigger
curl http://localhost:5100/patches/default/ReleaseOrderWorkflow/audit-before-decision
```

```json
{
  "summary": { "phase": 1, "outcome": 1, "nextPhase": 2, "blockingExecutionCount": 2 },
  "lastVerdict": {
    "blockingSample": ["release-order-8017", "release-order-8016"],
    "reason": "2 ejecución(es) pre-patch abiertas sin el marker"
  }
}
```

Se lee: el patch está en Convivencia, todavía **no** se puede pasar a `DeprecatePatch`, y estas son
las dos ejecuciones que lo impiden.

Override manual (solo saltos legales 1→2 o 2→3; cualquier otro devuelve `409` salvo `?force=true`):

```powershell
curl -X POST http://localhost:5100/patches/default/ReleaseOrderWorkflow/audit-before-decision/override `
  -H "Content-Type: application/json" `
  -d '{ "phase": 2, "declaredBy": "ramiro" }'
```

Sin `expiresAt`, el override vence a las `API_OVERRIDE_DEFAULT_TTL_HOURS` (24 h por defecto).

---

## Configuración

Todas las variables son opcionales; un valor ausente, no numérico o no positivo cae al default.

| Variable | Default | Qué controla |
| -------- | ------- | ------------ |
| `TEMPORAL_HOST` | `temporal:7233` | Cluster **propio** del monitor |
| `MONITOR_TASK_QUEUE` | `patch-monitor-task-queue` | Task queue del worker |
| `TARGET_TEMPORAL_HOST` | `temporal:7233` | Cluster **observado** |
| `TARGET_TEMPORAL_NAMESPACE` | `default` | Namespace observado |
| `DISCOVERY_LOOKBACK_DAYS` | `7` | Ventana de ejecuciones cerradas a considerar |
| `DISCOVERY_MAX_EXECUTIONS` | `500` | Tope de ejecuciones listadas por corrida |
| `DISCOVERY_MAX_HISTORIES` | `200` | Tope de Event Histories leídas por corrida |
| `PHASE_CLEAN_GRACE_HOURS` | `24` | Margen para inferir fase 3 |
| `PHASE_CLEAN_GRACE_MINUTES` | — | Si está y es positiva, reemplaza a la de horas (pruebas) |
| `MONITOR_SCHEDULE_ID` | `patch-monitor-schedule` | Id del Schedule (worker y API deben coincidir) |
| `MONITOR_INTERVAL_MINUTES` | `5` | Cadencia del Schedule |
| `MONITOR_CATCHUP_WINDOW_MINUTES` | `10` | Ventana para recuperar ticks perdidos |
| `MONITOR_MAX_PATCHES_PER_RUN` | `50` | Patches evaluados por pasada |
| `PATCH_STATE_CAN_THRESHOLD` | `500` | Assessments antes del `Continue-As-New` de un entity |
| `PATCH_STATE_HISTORY_LIMIT` | `20` | Cambios que se conservan en el historial de un patch |
| `NOTIFIER_ENABLED` | `true` | Apaga todas las notificaciones |
| `NOTIFIER_WEBHOOK_URL` | — | Si está, se hace `POST` a esa URL en cada cambio |
| `NOTIFIER_WEBHOOK_AUTH_HEADER` | — | Cabecera de auth, formato `Nombre: valor` |
| `NOTIFIER_WEBHOOK_TIMEOUT_SECONDS` | `10` | Timeout del webhook |
| `NOTIFIER_MAX_ATTEMPTS` | `3` | Reintentos de la notificación |
| `API_MAX_LIST_PATCHES` | `100` | Tope de `GET /patches` |
| `API_OVERRIDE_DEFAULT_TTL_HOURS` | `24` | Vencimiento por defecto de un override |

---

## Estructura del repositorio

```text
src/
  Contracts/     Dominio (fases, gates, veredictos), puertos e interfaces de workflow. Sin I/O.
  Common/        Plumbing de Temporal: WorkerHost, ScheduleBootstrapper, store de estado.
  PatchMonitor/  Worker: MonitorWorkflow, PatchStateWorkflow, PatchRegistryWorkflow, activities.
  MonitorApi/    API mínima de ASP.NET Core.
test/PatchMonitor.Tests/   xUnit + entorno time-skipping de Temporalio (sin Docker).
docker/          docker-compose.yml, overlay e2e y Dockerfiles.
scripts/e2e/     seed-orders.ps1, drain-orders.ps1, snapshot.ps1 (validación contra ReleaseOrderDemo).
docs/e2e/        Guía de fases aplicada y evidencia JSON del recorrido end-to-end.
specs/           Specs 01-09 (Spec-Driven Design).
Construction.md  Hoja de ruta, decisiones cerradas y criterio de "listo".
```

## Desarrollo

```powershell
dotnet build PatchMonitor.sln
dotnet test  PatchMonitor.sln     # 263 tests, sin Docker
```

La primera corrida de tests descarga el test-server de Temporal (una vez; queda cacheado).

## Validación end-to-end

El spec 09 apuntó el monitor al proyecto real
[`ReleaseOrderDemo`](https://github.com/Ramiro145/ReleaseOrderDemo) y recorrió las tres fases del
patch `audit-before-decision` contra su cluster:

| Evidencia (`docs/e2e/evidence/`) | Fase | Gate |
| -------------------------------- | ---- | ---- |
| `00-baseline` | no descubierto | — |
| `01-phase1-blocked` | Coexistence | Blocked (2 pre-patch abiertas) |
| `02-phase1-open` | Coexistence | Ready |
| `03-phase2-blocked` | Deprecated | Blocked (3 abiertas con marker) |
| `04-phase2-open` | Deprecated | Ready |
| `05-phase3-clean` | Clean | fase final |

Cero cambios de código en el monitor para apuntarlo ahí. El procedimiento está en
`specs/09-multi-target-e2e-validation.md` y `docs/e2e/releaseorder-patch-phases.md`.

## Límites conocidos

- **Un namespace y un cluster por instancia.** Para observar varios, corré una instancia por cada uno.
- **El costo escala con el tamaño del namespace**, no con la cantidad de patches: el descubrimiento
  no filtra por workflow type. Ajustá `DISCOVERY_*` antes de confiar en el resultado.
- **Convención de marker `core_patch`**: la emiten los SDKs basados en sdk-core (como el de .NET).
  Con otro SDK, verificá la convención antes.
- Sin validar todavía contra un cluster real: varios patches simultáneos, el webhook contra un
  endpoint real, y carga sostenida de días.
- El auto-versionado del propio monitor (spec 10) está diferido.

Detalle en `specs/09-multi-target-e2e-validation.md`, sección "Límites conocidos".
