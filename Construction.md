# Construction.md — hoja de ruta de specs

Este documento define **qué specs se van a escribir, en qué orden y con qué objetivo cerrado cada
uno**. No es una spec ni contiene planes de implementación: es el mapa que se sigue al ejecutar
`/spec 01 …`, `/spec 02 …`, para que cada spec entre con contexto suficiente y sin re-litigar
decisiones ya tomadas.

Fuente de la verdad del dominio y de las versiones: el proyecto de referencia
`ReleaseOrderDemo` (`https://github.com/Ramiro145/ReleaseOrderDemo`, copia local en
`C:\Users\ADMIN\Desktop\Tecnoap\Frisa\releaseorder-signal\releaseorder_combined`).

---

## 1. Objetivo del proyecto

Un **monitor genérico del ciclo de vida de los patches de Temporal**.

Un "patch" es un **patch de Temporal** en el sentido de `Workflow.Patched(...)` /
`Workflow.DeprecatePatch(...)`: el mecanismo para cambiar el código de un Workflow que tiene
ejecuciones vivas sin romperlas por no-determinismo. Su ciclo de vida es una **máquina de estados
fija y conocida** de tres fases:

| Fase | Nombre | Código en el Workflow | Se sale cuando… |
|------|--------|-----------------------|-----------------|
| 1 | Convivencia | `if (Workflow.Patched("id")) { nuevo } else { viejo }` | no queda **ninguna ejecución pre-patch abierta** |
| 2 | Deprecación | `Workflow.DeprecatePatch("id");` + paso incondicional | no queda **ninguna ejecución abierta con el marker** |
| 3 | Código limpio | el paso sin ninguna mención al patch | (fase final) |

Lo **genérico** no es la máquina de estados —esa está fijada por Temporal— sino **contra qué se
apunta**: el monitor se conecta a cualquier namespace de Temporal, **descubre** qué `patchId` hay en
juego, **resuelve** en qué fase está cada uno, y cada 5 minutos **evalúa** si ya se cumple el
criterio para saltar a la fase siguiente.

Referencia visual del ciclo completo y de los errores típicos: artifact `202830f1`
(«Ciclo de vida de un patch de Workflow»).

---

## 2. Qué hace y qué NO hace el monitor

| Hace | No hace |
|------|---------|
| Descubre los `patchId` activos en un namespace | Modificar código fuente (`Patched` → `DeprecatePatch` es una edición humana + redeploy) |
| Resuelve la fase actual (1 / 2 / 3) de cada patch | Abrir PRs, disparar CI, ni ejecutar el redeploy |
| Evalúa los criterios de salto de fase cada 5 min (Temporal Schedule, **sin polling**) | Tocar ninguna BD de negocio ni aplicar efectos de dominio (SAGA) |
| Registra el veredicto de forma durable y lo expone (API + `[WorkflowQuery]`) | Decidir por su cuenta: el salto de fase lo hace una persona, con el veredicto como insumo |
| Notifica **una vez** cuando el veredicto de un patch cambia (webhook / log) | Reintentar o "arreglar" workflows del proyecto observado |

El "componente que ejecuta la transición" del README se materializa acá como **registrar +
notificar**, no como aplicar el cambio. El cambio de fase de un patch de Temporal es intrínsecamente
una acción sobre el código fuente, fuera del alcance de un programa.

---

## 3. Decisiones de arquitectura ya cerradas

Estas decisiones **no se re-discuten** en los specs; se citan.

| Decisión | Valor | Por qué |
|----------|-------|---------|
| Qué es un patch | Patch de Temporal (`Workflow.Patched`), máquina de estados fija de 3 fases | Es la lectura que sostiene el artifact y el requisito de "no adaptarse a un solo proyecto" |
| Rol del ejecutor | Registrar el veredicto + exponerlo + notificar | Un programa no puede editar código fuente ni redesplegar; sería deshonesto prometerlo |
| Descubrimiento de patches | Dos niveles: query sobre el search attribute `TemporalChangeVersion`, con **fallback** a escaneo de Event History | La standard visibility sobre Postgres (stack del repo de referencia) falla en queries compuestas de `KeywordList` |
| Persistencia del estado del monitor | **Entity Workflows en Temporal**, sin BD propia; puerto `IDecisionSink` para un sink SQL futuro | Cero infraestructura extra ⇒ barrera de adopción mínima; un entity workflow abierto nunca lo purga la retención; Temporal ya es un store durable con la semántica exacta |
| Auditoría histórica de largo plazo | Fuera de alcance del núcleo; se cubre a futuro con un adaptador `IDecisionSink` aditivo | `Continue-As-New` trunca la Event History; si hace falta consultar "hace 8 meses", va a un sink dedicado, sin reescribir el núcleo |
| Entregables extra | API mínima de control + notificador pluggable | Sin frontend, sin spec dedicado de tests (cada spec lleva los suyos) |
| Stack | `.NET 8`, `Temporalio` 1.9.0, `temporalio/auto-setup:1.23.0`, `temporalio/ui:2.23.0`, Postgres 15, Docker Compose. **Sin SQL Server.** | Igualar el repo de referencia; SQL Server sale porque no hay BD propia |

---

## 4. Restricciones técnicas conocidas

Las trampas donde se pierde tiempo si no están escritas de antemano:

1. **`TemporalChangeVersion` es `KeywordList`.** Lo escribe `Patched` automáticamente
   (`<patchId>-<version>`). Consultarlo por *standard visibility* sobre Postgres da
   `context deadline exceeded` de forma reproducible en queries compuestas → de ahí el descubrimiento
   en dos niveles (spec 03). Las queries simples (`ExecutionStatus='Running'`, `--limit N`) sí
   responden.
2. **La Event History NO distingue fase 1 de fase 2 por presencia del marker.** Fase 1 y fase 2
   escriben ambas `MarkerRecorded · core_patch`. Lo único que cambia es el flag `deprecated` dentro
   del marker. Asumir que `DeprecatePatch` deja de escribir el marker es un error típico documentado
   en el artifact → resolver la fase actual es un problema propio (spec 04).
3. **Los criterios de salto son asimétricos.** 1→2 mira ejecuciones **abiertas sin marker**
   (pre-patch). 2→3 mira que **no quede ninguna ejecución abierta con el marker**. Son dos predicados
   distintos, no el mismo con un parámetro.
4. **Un entity workflow nunca cierra.** Si el monitor guarda su estado en entity workflows, esas
   ejecuciones jamás pasarían el gate 1→2 de sus propios patches. `Continue-As-New` por umbral de
   historia es lo que lo resuelve → es requisito del spec 05, no un ajuste de performance, y es la
   razón por la que el spec 10 es posible.
5. **`temporal workflow list` con `--query` compuesta se cuelga**, pero listar por
   `ListWorkflowsAsync` con filtros simples y luego `FetchHistoryAsync` por ejecución funciona. El
   descubrimiento y la resolución de fase se construyen sobre esa base, con un tope de ejecuciones
   inspeccionadas por corrida.
6. **`Common/` del repo de referencia se reutiliza casi tal cual**: `WorkerHost` (drenaje SIGTERM,
   `GracefulShutdownTimeout` 30 s, activities resueltas por tipo concreto desde DI),
   `WorkflowStarter`, `WorkflowValidator` (wrapper de `DescribeAsync` que distingue "not found" de
   otros errores RPC). Copiar, no reinventar.

---

## 5. Mapa de proyectos

Layout objetivo bajo `src/`, todos `net8.0`, `Temporalio` 1.9.0:

| Proyecto | Rol |
|----------|-----|
| `Contracts` | Interfaces de workflow compartidas, DTOs, puertos (`IPatchDiscovery`, `IPhaseResolver`, `INotifier`, `IDecisionSink`). Todo lo demás depende de este. |
| `Common` | Plumbing genérico de Temporal, portado del repo de referencia: `WorkerHost`, `WorkflowStarter`, `WorkflowValidator`, más `ScheduleBootstrapper` (nuevo, spec 06). |
| `PatchMonitor` | Worker: clases `[Workflow]` / `[Activity]` del monitor. `MonitorWorkflow`, `PatchStateWorkflow` (entity), `PatchRegistryWorkflow` (índice). Activities respaldadas por `Services/*`. |
| `MonitorApi` | API mínima de ASP.NET Core: único punto de entrada HTTP. `TemporalClient` singleton (`TEMPORAL_HOST`, default `temporal:7233`). |

Docker: carpeta `docker/` con un Dockerfile por servicio + `docker-compose.yml`. El worker lleva
`stop_grace_period` mayor que los 30 s de `GracefulShutdownTimeout`.

---

## 6. Convención de specs

Derivada de las 6 specs de `ReleaseOrderDemo`. Cada `/spec` que se cree debe respetarla al pie.

- **Sin frontmatter YAML.** Header:

  ```markdown
  # NN - Título de la spec

  **Estado:** Borrador
  **Depende de:** [NN-slug-anterior.md](NN-slug-anterior.md)   (o `-` si no depende de ninguna)
  **Fecha:** AAAA-MM-DD

  **Objetivo:** Una sola frase (puede envolverse a ~100 columnas).
  ```

- **Secciones `##`, en este orden exacto** (nunca `###` para las canónicas):
  1. `## Por qué existe este spec` — prosa, 2-5 párrafos. El porqué, no el qué. Cita specs
     anteriores y qué dejaron pendiente. Se puede omitir solo en specs triviales.
  2. `## Alcance` — dos sub-bloques en negrita (no encabezados):
     `**Incluye:**` (viñetas con rutas de archivo reales y nombres concretos, con bloques de código
     si ayuda) y `**No incluye (fuera de alcance de este spec):**`.
  3. `## Modelo de datos` — estructuras concretas (```csharp / ```sql / árbol de carpetas / tabla).
     Si no hay datos nuevos, decirlo explícitamente.
  4. `## Plan de implementación` — lista **numerada**. Cada paso: **título en negrita seguido de
     punto**, luego la explicación. 6-12 pasos, cada uno commiteable y dejando el sistema funcional;
     si un paso supera ~30-50 líneas de código, se parte. El **último paso es siempre verificación
     end-to-end manual**.
  5. `## Criterios de aceptación` — checklist booleana `- [ ]`.
  6. `## Decisiones tomadas y descartadas` — viñetas con prefijo `**Sí:**` / `**No:**` + una
     justificación breve.
  7. `## Riesgos identificados` — tabla `| Riesgo | Mitigación |`.
  8. `## Qué NO entra en este spec` — refuerzo final, viñetas, cerrando con la frase literal
     `Cada uno, si entra, va en su propio spec.`

- **Tamaño**: 130-350 líneas, típico ~230.
- **Estados**: `Borrador` → `Aprobado` → `Implementado`. `/spec` crea siempre en `Borrador`. El
  salto a `Aprobado` lo hace una persona. `/spec-impl` solo corre sobre specs `Aprobado`, y al
  terminar deja la spec en `Implementado`.
- **Ramas**: `/spec-impl` crea `spec-NN-slug` (nombre en **inglés**, derivado del archivo) según
  `AutoCreateBranch` de `specs/.spec-config.yml`. Nunca commitea por su cuenta.
- **Idioma**: la prosa de las specs va en **español**; los identificadores de código (clases,
  métodos, interfaces, slugs de spec, ramas) en **inglés**.
- Antes de escribir una spec nueva, leer las **dos más recientes** para mantener convenciones.

---

## 7. La secuencia de specs

| NN | Slug (rama = `spec-NN-slug`) | Objetivo en una frase | Depende de |
|----|------------------------------|-----------------------|-----------|
| 01 | `solution-scaffolding-docker-stack` | Solución .NET 8 multi-proyecto que compila y arranca, con el stack de Docker de Temporal del repo de referencia | – |
| 02 | `patch-lifecycle-domain-model` | El vocabulario y los dos gates de las 3 fases como código puro, sin I/O ni Temporal | 01 |
| 03 | `patch-discovery-two-tier` | Descubrir los `patchId` en juego: query sobre `TemporalChangeVersion` con fallback a Event History | 02 |
| 04 | `current-phase-resolution` | Resolver si un patch está en fase 1, 2 o 3, leyendo el flag `deprecated` del marker | 03 |
| 05 | `durable-state-entity-workflows` | Estado durable sin BD: entity workflow por patch + registry singleton como índice, con Continue-As-New | 04 |
| 06 | `monitor-workflow-temporal-schedule` | El disparo cada 5 minutos vía Temporal Schedule, sin polling | 05 |
| 07 | `pluggable-notifier` | Notificar **una sola vez** por cambio de veredicto, vía webhook o log estructurado | 06 |
| 08 | `control-api` | Superficie HTTP única: listar, consultar, chequear on-demand, pausar/reanudar, override manual de fase | 07 |
| 09 | `multi-target-e2e-validation` | Apuntar el monitor al `ReleaseOrderDemo` real y reproducir el recorrido de fases del artifact | 08 |
| 10 | `self-versioning-and-drain` | Aplicar el ciclo `Patched → DeprecatePatch → limpio` al propio `MonitorWorkflow` — **diferido, ver nota abajo** | 09 |

### Por qué van en ese orden

**01 — Scaffolding + stack de Docker.**
Ningún spec posterior es verificable sin un `docker compose up` que levante Temporal, la UI y un
worker que loguee. Entrega los 4 proyectos vacíos que compilan, `Common/` portado del repo de
referencia (incluido el drenaje SIGTERM y `stop_grace_period: 45s`), y el `docker-compose.yml`.
**Sin SQL Server** — es la consecuencia visible de la decisión de persistencia.

**02 — Modelo de dominio del ciclo de vida.**
Va antes que cualquier cosa que hable con Temporal. Fija los tipos (`PatchPhase`, `PatchKey` =
namespace + workflowType + patchId, `ExecutionSnapshot`, `PhaseVerdict`) y los dos gates
(`CoexistenceToDeprecatedGate`, `DeprecatedToCleanGate`) como **funciones puras** sobre una lista de
snapshots. Cero I/O ⇒ testeable sin Docker ni cluster. Es el corazón de la lógica y donde vive la
asimetría de los criterios de salto (restricción #3).

**03 — Descubrimiento en dos niveles.**
El único punto que habla con Temporal *como fuente de datos* (`ListWorkflowsAsync`,
`FetchHistoryAsync`). Detrás del puerto `IPatchDiscovery` queda reemplazable y testeable con
fixtures de Event History. Acá vive el fallback query → escaneo (restricciones #1, #5) y el tope de
ejecuciones inspeccionadas por corrida.

**04 — Resolución de la fase actual.**
Separada de 03 a propósito: 03 extrae datos crudos, 04 los interpreta. El flag `deprecated` del
marker es exactamente el punto que el artifact marca como error típico (restricción #2); mezclarlo
con el descubrimiento entierra la sutileza. Incluye el override manual de fase declarado por el
operador (se consume en specs 05 y 08).

**05 — Estado durable en entity workflows.**
Recién acá hay algo que valga la pena persistir. Entrega `PatchStateWorkflow` (entity, `WorkflowId`
determinístico por `PatchKey`, arranque por signal-with-start, `[WorkflowQuery]` para lectura viva,
**Continue-As-New** por umbral de historia — restricción #4), `PatchRegistryWorkflow` (índice
singleton, para que "listar todos los patches" nunca dependa de Visibility) y el puerto
`IDecisionSink` (no-op por defecto; el adaptador SQL es trabajo futuro).

**06 — MonitorWorkflow + Temporal Schedule.**
Primero tiene que existir *qué* ejecutar; después, el reloj. Entrega `MonitorWorkflow` (une 03 + 04
+ 05 en una pasada) y `ScheduleBootstrapper` en `Common`: creación idempotente (capturar
`ScheduleAlreadyRunningException`), `ScheduleIntervalSpec(TimeSpan.FromMinutes(5))`,
`Overlap = ScheduleOverlapPolicy.Skip`, `CatchupWindow`. Cumple el "no pullear" del README.

**07 — Notificador pluggable.**
Depende de 05 para saber si un veredicto **cambió**: notificar por corrida spamearía cada 5 minutos.
`INotifier` con implementaciones registradas por DI (webhook HTTP, log estructurado); un fallo de
notificación nunca rompe el monitoreo (se compensa/ignora, no propaga).

**08 — API de control.**
Al final de la cadena funcional porque expone lo que los specs anteriores construyeron: listar
patches y su fase, consultar el veredicto de uno, disparar el chequeo on-demand
(`TriggerImmediatelyAsync`), pausar/reanudar el Schedule, y el **override manual de fase** vía
`[WorkflowUpdate]` + `[WorkflowUpdateValidator]` (patrón del `ReleaseOrderWorkflow`). Reusa
`WorkflowValidator` para distinguir "not found".

**09 — Validación multi-target end-to-end.**
El spec que demuestra que el objetivo se cumplió: el monitor y `ReleaseOrderDemo` en la misma red de
Docker, el monitor apuntado a su namespace, reproduciendo el recorrido de fases del artifact
(ejecuciones vivas → fase 1; drenaje → gate 1→2 se abre; etc.). Si este spec pasa, el proyecto
cumple el README.

**10 — El monitor se versiona a sí mismo.**
Cierra el círculo y es el caso extremo del problema que el proyecto monitorea: los entity workflows
del spec 05 **nunca cierran**, así que cambiarles el código exige `Workflow.Patched` sí o sí, y el
gate 1→2 solo se habilita gracias al `Continue-As-New` que introdujo el 05. Entrega el recorrido de
3 fases aplicado al `MonitorWorkflow` + la verificación del drenaje ordenado bajo
`docker compose stop`.

> **Diferido (decidido con el usuario el 2026-09-11).** El spec 09 ya demostró con evidencia real
> que el objetivo central del README se cumple: el monitor apunta a un proyecto ajeno sin tocar su
> código. Antes de invertir en este spec 10 (que es introspectivo — versiona el propio monitor, no
> agrega capacidad de observar proyectos nuevos), la prioridad pasa a ser **probar el monitor contra
> un segundo proyecto real distinto de `ReleaseOrderDemo`**, que es lo que efectivamente valida la
> promesa de "genérico, reusable en otros proyectos". Ver `specs/09-multi-target-e2e-validation.md`,
> sección "Límites conocidos para reusar esto en otro proyecto", para la lista concreta de qué mirar
> en esa próxima prueba. Este spec 10 queda documentado y listo para retomarse — no se descarta, solo
> se re-prioriza detrás de esa validación.

### Testing

No hay un spec dedicado a tests. Cada spec lleva sus pruebas en `## Criterios de aceptación`:

- Specs **02, 03, 04**: unitarios puros, sin Docker (funciones sobre fixtures de snapshots / Event
  History sintética).
- Specs **05 en adelante**: entorno de **time-skipping de `Temporalio`**, igual que
  `test/ReleaseOrder.Tests` del repo de referencia. El binario del test-server se descarga una vez y
  queda cacheado.

---

## 8. Criterio de "listo"

El proyecto se da por terminado cuando:

> **Nota (2026-09-11):** el spec 10 quedó diferido, no abandonado — ver la nota en §7, ítem 10, y
> [[spec10-deferred-validate-elsewhere-first]]. Los dos ítems que dependen de él (el primero y el
> último de esta lista) quedan sin marcar por eso, no porque algo haya fallado. Los cinco de en
> medio ya están verificados con evidencia real de los specs 01-09.

- [ ] Los 10 specs están en estado `Implementado`. *(01-09 sí, verificado: todos dicen
      `Implementado`/`Implementada`. El 10 está diferido — ver nota arriba — y ni siquiera tiene
      archivo de spec todavía.)*
- [x] `docker compose up` levanta Temporal + UI + `PatchMonitor` + `MonitorApi` sin SQL Server.
      *(Confirmado repetidas veces durante el spec 09: los 5 servicios de `docker-compose.yml`
      quedan sanos — `temporal`, `temporal-db` (Postgres), `temporal-ui`, `patch-monitor-worker`,
      `monitor-api` — sin ningún servicio de SQL Server.)*
- [x] El monitor, apuntado a un namespace arbitrario, lista sus patches y la fase de cada uno sin
      configuración específica de ese proyecto. *(Spec 09: apuntado al namespace `default` de
      `ReleaseOrderDemo`, un repo ajeno, solo con `TARGET_TEMPORAL_HOST`/`TARGET_TEMPORAL_NAMESPACE`
      — cero cambios de código. `GET /patches` reportó correctamente las 3 fases del patch real. Los
      topes de descubrimiento (`DISCOVERY_LOOKBACK_DAYS` y similares) sí conviene dimensionarlos por
      proyecto — ver `specs/09-...md` §"Límites conocidos" — pero eso es afinar una perilla
      genérica, no escribir configuración específica del proyecto observado.)*
- [x] El Schedule corre cada 5 minutos y no hay ningún bucle de polling en el código. *(Spec 09,
      paso 10: corridas naturales de `MonitorWorkflow` en `21:55`, `22:00`, `22:05`, `22:10`,
      `22:15` — cada 5 minutos exactos, sin gaps ni superposición, sin polling visible en los logs
      entre ticks.)*
- [x] Un cambio de veredicto genera exactamente una notificación, no una por corrida. *(Verificado
      en el mismo paso 10: cada uno de los 7 cambios reales de revisión generó exactamente 1
      notificación, y las corridas del Schedule sin cambio de estado generaron 0. No confundir con
      el conteo total del recorrido — ese es un criterio distinto, ya corregido en
      `specs/09-...md`, que hablaba de "dos notificaciones en todo el recorrido" y no de "una por
      cambio".)*
- [x] El escenario del artifact (fase 1 → gate 1→2 → fase 2) se reproduce end-to-end contra
      `ReleaseOrderDemo` (spec 09). *(Reproducido de punta a punta, incluida la fase 3: evidencia en
      `docs/e2e/evidence/00-baseline.json` a `05-phase3-clean.json`.)*
- [ ] El `MonitorWorkflow` no tiene ningún `Workflow.Patched` ni `Workflow.DeprecatePatch` residual
      tras el spec 10, y el drenaje bajo `docker compose stop` respeta los 30 s. *(Bloqueado por el
      spec 10 diferido — no aplica todavía.)*
