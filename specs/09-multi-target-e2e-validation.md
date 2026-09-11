# 09 - Validación multi-target end-to-end

**Estado:** Aprobado
**Depende de:** [08-control-api.md](08-control-api.md)
**Fecha:** 2026-09-11

**Objetivo:** Apuntar el monitor al `ReleaseOrderDemo` real y reproducir end-to-end el recorrido
completo de fases del artifact «Ciclo de vida de un patch de Workflow», demostrando que el proyecto
cumple el README.

## Por qué existe este spec

Los specs 01 a 08 dejaron un monitor completo — descubre, resuelve fase, evalúa gates, persiste,
notifica y se opera por HTTP — pero **nunca contra un cluster que no sea el suyo**. Toda la
evidencia hasta hoy salió de `ProbeWorkflow`/`probe_patch`, artefactos efímeros y no versionados
que las specs 03, 04 y 08 solo citan en prosa; el código nunca entró al repo. Peor: el adaptador que
de verdad habla gRPC con Temporal, `TemporalExecutionSource`, no tiene ni un test automatizado —
toda su cobertura son fakes de `IExecutionSource` sobre fixtures de Event History sintética
(`test/PatchMonitor.Tests/Discovery/HistoryFixtures.cs`). `Construction.md` §7 fija este spec como
el que cierra esa brecha: "el spec que demuestra que el objetivo se cumplió".

Hay un problema de partida: `ReleaseOrderDemo`, el proyecto de referencia, **ya no tiene ningún
patch activo**. El patch `audit-before-decision` recorrió sus tres fases en los specs 04, 05 y 06 de
ese repo y se borró (commit `f4d4148`); su `ReleaseOrderPatchingTests` asierta hoy
`CountMarkers("core_patch") == 0`. Apuntar el monitor tal cual descubriría cero patches y no
probaría nada. La decisión tomada es **reintroducir el mismo patch en el repo real**, desplegando
sus tres variantes de código una por una — no fabricar un target sintético dentro de este repo, que
dejaría de ser "el `ReleaseOrderDemo` real" que pide `Construction.md` §1, y no reusar
`ProbeWorkflow`, que nunca existió como código versionado y no ejercitaría el `WorkflowType` real
que un operador va a monitorear en producción.

El segundo problema es de configuración. Ambos proyectos son stacks de Docker Compose
independientes, cada uno con su propio cluster de Temporal, y los dos tienen un servicio llamado
`temporal` — compartir red los haría colisionar por DNS. `docker-compose.yml` del monitor ya viene
preparado para esta situación (`name: patchmonitor`, puertos corridos 7234/8234/5433 frente a los
7233/8233/5432 de `ReleaseOrderDemo`, comentado explícitamente "para spec 09"), pero
`TARGET_TEMPORAL_HOST` sigue apuntando por default al propio cluster. La conectividad elegida es
`host.docker.internal:7233`: el puerto que `ReleaseOrderDemo` ya publica en el host, sin red
compartida y sin tocar una sola línea de ese repo para la conexión en sí.

El tercer problema es de tiempo. `PhaseOptions.CleanGrace` solo se configura en horas
(`PHASE_CLEAN_GRACE_HOURS`, default 24): con un Schedule de 5 minutos, cerrar el criterio de fase 3
tardaría más de un día de reloj. Este spec agrega una unidad más fina — sin tocar la cadencia del
Schedule, que el README exige en 5 minutos — para que el recorrido completo cierre en una sesión de
trabajo.

## Alcance

**Incluye:**

- **`src/Contracts/Discovery/DiscoveryOptions.cs`** _(modificado)_ — nuevo campo `string TargetHost`
  en el record, leído de `TARGET_TEMPORAL_HOST` con el mismo criterio "ausente/blanco ⇒ default,
  nunca lanza" que ya usa `Namespace`:

  ```csharp
  public sealed record DiscoveryOptions(
      string Namespace, string TargetHost, int LookbackDays, int MaxExecutions, int MaxHistories)
  {
      public const string DefaultTargetHost = "temporal:7233";
      // ... resto igual
  }
  ```

  Cierra el único punto de configuración del proyecto que hoy escapa al patrón `*Options.FromEnvironment()`:
  `TARGET_TEMPORAL_HOST` se lee inline en `TemporalExecutionSource.ConnectAsync()` y no tiene
  cobertura de tests. Es justo la variable que este spec ejercita a fondo.

- **`src/PatchMonitor/Services/TemporalExecutionSource.cs`** _(modificado)_ — `ConnectAsync()` usa
  `_options.TargetHost` en vez de `Environment.GetEnvironmentVariable("TARGET_TEMPORAL_HOST")`.
  Sin cambio de comportamiento por default; sí cambia que ahora es testeable sin variables de
  entorno de proceso.

- **`src/Contracts/Phase/PhaseOptions.cs`** _(modificado)_ — nueva `PHASE_CLEAN_GRACE_MINUTES`, con
  precedencia sobre `PHASE_CLEAN_GRACE_HOURS` cuando ambas están presentes:

  ```csharp
  public sealed record PhaseOptions(TimeSpan CleanGrace)
  {
      public const int DefaultCleanGraceHours = 24;

      public static PhaseOptions FromEnvironment()
      {
          var minutesRaw = Environment.GetEnvironmentVariable("PHASE_CLEAN_GRACE_MINUTES");
          if (int.TryParse(minutesRaw, out var minutes) && minutes > 0)
              return new PhaseOptions(TimeSpan.FromMinutes(minutes));

          return new PhaseOptions(TimeSpan.FromHours(
              PositiveIntOrDefault("PHASE_CLEAN_GRACE_HOURS", DefaultCleanGraceHours)));
      }
  }
  ```

  Ninguna de las dos variables es obligatoria; el compose base sigue sin declarar
  `PHASE_CLEAN_GRACE_MINUTES` y se comporta exactamente igual que hoy.

- **`docker/docker-compose.e2e.yml`** — overlay que se aplica **encima** del compose base
  (`docker compose -f docker-compose.yml -f docker-compose.e2e.yml up -d`), sin tocarlo:

  ```yaml
  services:
    patch-monitor-worker:
      extra_hosts:
        - "host.docker.internal:host-gateway"
      environment:
        - TARGET_TEMPORAL_HOST=host.docker.internal:7233
        - TARGET_TEMPORAL_NAMESPACE=default
        - PHASE_CLEAN_GRACE_MINUTES=3
        - DISCOVERY_LOOKBACK_DAYS=1
    monitor-api:
      environment:
        - TARGET_TEMPORAL_HOST=host.docker.internal:7233
  ```

  `host-gateway` es soporte nativo de Docker Desktop/Compose para Windows y Linux recientes; no
  requiere el paquete `extra_hosts` manual con IP fija. `DISCOVERY_LOOKBACK_DAYS=1` acota el ruido
  de ejecuciones cerradas ajenas al patch bajo prueba, ya que el descubrimiento no filtra por
  `WorkflowType` (decisión del spec 03: genérico a propósito).

- **`scripts/e2e/`** — primera carpeta de scripts del repo, PowerShell, contra la API de
  `ReleaseOrderDemo` (puerto 5000) y `MonitorApi` (puerto 5100):
  - **`seed-orders.ps1 -Count N`** — `POST /orders` + `POST /orders/{id}/release` por cada orden,
    sin mandar decisión: quedan `Running`, en `Waiting for release decision`, sin marker si el
    worker todavía corre código pre-patch.
  - **`drain-orders.ps1 -OrderIds ...`** — `POST /orders/{id}/release/decision` con
    `{"approved": true}` para cada id: usa la Signal (sin validador de estado), no el Update.
  - **`snapshot.ps1 -Label <texto>`** — `POST /schedule/trigger` contra `MonitorApi`, espera unos
    segundos, y vuelca `GET /patches` + `GET /patches/{ns}/{type}/patchId` a
    `docs/e2e/evidence/<timestamp>-<Label>.json`. Es la evidencia que el paso 10 pega en esta spec.

- **`docs/e2e/releaseorder-patch-phases.md`** — los tres diffs exactos sobre
  `src/ReleaseOrder/Workflows/ReleaseOrderWorkFlow.cs` de `ReleaseOrderDemo`
  (`releaseorder-signal/releaseorder_combined`, no este repo), tomados literalmente de sus propios
  specs 04/05/06, más el comando para reconstruir y redesplegar `release-orden-worker` entre cada
  fase:

  ```
  docker compose -f docker/docker-compose.yml build release-orden-worker
  docker compose -f docker/docker-compose.yml up -d --force-recreate release-orden-worker
  ```

  Fase 1 (spec 04 de `ReleaseOrderDemo`):

  ```csharp
  if (Workflow.Patched("audit-before-decision"))
  {
      await Workflow.ExecuteActivityAsync(
          (AuditActivities a) => a.RecordAwaitingDecisionAsync(orderId),
          DefaultOptions);
  }
  ```

  Fase 2 (spec 05 de ese repo): reemplazar el `if` por
  `Workflow.DeprecatePatch("audit-before-decision");` seguido del mismo `ExecuteActivityAsync`
  incondicional. Fase 3 (spec 06 de ese repo): borrar la línea de `DeprecatePatch`, dejar el
  `ExecuteActivityAsync` solo. Este documento vive en `docs/` y no en el repo ajeno porque ese
  código no es nuestro: aplicarlo y commitearlo en `ReleaseOrderDemo` es una acción manual del
  operador, documentada acá paso a paso.

- **`test/PatchMonitor.Tests/Discovery/DiscoveryOptionsTests.cs`** _(modificado)_ — casos para
  `TARGET_TEMPORAL_HOST`: ausente ⇒ `temporal:7233`; en blanco ⇒ default; con valor ⇒ respetado
  (mismo patrón que ya cubre `TARGET_TEMPORAL_NAMESPACE`).

- **`test/PatchMonitor.Tests/Phase/PhaseOptionsTests.cs`** _(modificado)_ — casos para
  `PHASE_CLEAN_GRACE_MINUTES`: ausente ⇒ cae a la lógica de horas existente; presente y positiva ⇒
  gana sobre `PHASE_CLEAN_GRACE_HOURS` aunque ambas estén seteadas; no numérica o ≤ 0 ⇒ cae a horas,
  nunca lanza.

**No incluye (fuera de alcance de este spec):**

- **Monitorear varios namespaces o varios clusters en la misma corrida.** `Construction.md` §7 dice
  "multi-target" en el sentido de "el target ya no es el propio cluster", no de paralelismo. Un
  namespace por env var sigue siendo la decisión cerrada del spec 03; agregar una lista de targets
  reabriría descubrimiento, estado y API por un caso de uso que nadie pidió.
- **Filtrar el descubrimiento por `WorkflowType`.** Sigue fuera de alcance por la misma razón que el
  spec 03 lo descartó: rompería la genericidad. El ruido del namespace ajeno se acota con
  `DISCOVERY_LOOKBACK_DAYS`, no con un filtro nuevo.
- **Compartir la red de Docker entre los dos stacks.** Exigiría resolver la colisión del hostname
  `temporal` con un alias y acoplaría el arranque de un stack a la existencia del otro.
  `host.docker.internal` evita ambos problemas.
- **Commitear los diffs de fase en el repo de `ReleaseOrderDemo`.** Es un repo ajeno
  (`gitlab.com/jmedina_tecnoap/temporalio` o el fork en `github.com/Ramiro145/ReleaseOrderDemo`,
  según la copia usada); este spec documenta el procedimiento, no lo ejecuta por su cuenta ni deja
  una rama a medio aplicar sin que el operador lo decida.
- **Automatizar el redeploy de las tres fases con un script.** Cada fase es una edición de código +
  rebuild manual, deliberadamente: automatizarlo escondería el paso más importante del recorrido
  (que el humano edita código fuente, no el monitor — `Construction.md` §2).
- **Tests de integración que requieran Docker en `dotnet test`.** Los dos únicos cambios de código
  de este spec (`TargetHost`, `PHASE_CLEAN_GRACE_MINUTES`) son parseo puro y se cubren como el resto
  de `*OptionsTests`, sin cluster.
- **Aplicar `Workflow.Patched`/`DeprecatePatch` sobre el propio `MonitorWorkflow`.** Es el spec 10.

## Modelo de datos

Sin tipos de dominio nuevos: este spec reconfigura y ejercita lo que los specs 01-08 ya definen. Lo
nuevo es superficie de configuración y de scripts.

Env vars nuevas:

| Variable                    | Default                                                                                     | Dónde se lee                                                                      |
| --------------------------- | ------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| `TARGET_TEMPORAL_HOST`      | `temporal:7233`                                                                             | `DiscoveryOptions.FromEnvironment()` (antes: inline en `TemporalExecutionSource`) |
| `PHASE_CLEAN_GRACE_MINUTES` | _(sin default propio — gana sobre `PHASE_CLEAN_GRACE_HOURS` si está seteada y es positiva)_ | `PhaseOptions.FromEnvironment()`                                                  |

Árbol de lo nuevo:

```text
proyecto_monitoreo/
├── docker/
│   └── docker-compose.e2e.yml
├── docs/
│   └── e2e/
│       ├── releaseorder-patch-phases.md
│       └── evidence/
│           └── <timestamp>-<label>.json      (generado por snapshot.ps1, paso 10)
├── scripts/
│   └── e2e/
│       ├── seed-orders.ps1
│       ├── drain-orders.ps1
│       └── snapshot.ps1
├── src/
│   ├── Contracts/
│   │   ├── Discovery/DiscoveryOptions.cs        (modificado: + TargetHost)
│   │   └── Phase/PhaseOptions.cs                (modificado: + minutos)
│   └── PatchMonitor/Services/TemporalExecutionSource.cs   (modificado)
└── test/PatchMonitor.Tests/
    ├── Discovery/DiscoveryOptionsTests.cs        (modificado)
    └── Phase/PhaseOptionsTests.cs                (modificado)
```

Recorrido esperado — lo que `GET /patches/{ns}/{type}/audit-before-decision` debe reportar en cada
etapa (`ns` = `default`, `type` = `ReleaseOrderWorkflow`):

| #   | Acción sobre `ReleaseOrderDemo`                                            | `Phase`                                                   | Gate                                            | `NextPhase`  |
| --- | -------------------------------------------------------------------------- | --------------------------------------------------------- | ----------------------------------------------- | ------------ |
| 0   | Stack arriba, `release-orden-worker` con código limpio (sin patch)         | _(no descubierto)_                                        | —                                               | —            |
| 1   | 2 órdenes arrancadas, sin decisión (pre-patch, abiertas, sin marker)       | _(sigue sin descubrirse: nada escribe el marker todavía)_ | —                                               | —            |
| 2   | Deploy fase 1 (`Patched`) + 2 órdenes nuevas abiertas                      | `Coexistence`                                             | `Blocked` (2 ejecuciones pre-patch bloqueantes) | `Deprecated` |
| 3   | Drenar las 2 órdenes pre-patch (`drain-orders.ps1`)                        | `Coexistence`                                             | `Open`                                          | `Deprecated` |
| 4   | Deploy fase 2 (`DeprecatePatch`) + 1 orden nueva abierta                   | `Deprecated`                                              | `Blocked` (ejecuciones con marker abiertas)     | `Clean`      |
| 5   | Drenar todas las ejecuciones con marker                                    | `Deprecated`                                              | `Open`                                          | `Clean`      |
| 6   | Deploy fase 3 (limpio) + 1 orden nueva, pasado `PHASE_CLEAN_GRACE_MINUTES` | `Clean`                                                   | _(fase final, sin gate)_                        | —            |

## Plan de implementación

1. **`TargetHost` en `DiscoveryOptions` + tests.** Agregar el campo y `DefaultTargetHost`, ajustar
   el único call site (`DiscoveryOptions.FromEnvironment()`), y `DiscoveryOptionsTests.cs` con los
   tres casos (ausente, blanco, con valor). `dotnet build` y `dotnet test` verdes.

2. **`TemporalExecutionSource` consume `TargetHost`.** Reemplazar la lectura inline de
   `TARGET_TEMPORAL_HOST` por `_options.TargetHost` (la clase ya recibe `DiscoveryOptions` por
   constructor). Ningún test de este archivo cambia de aserción porque el default no cambia.

3. **`PHASE_CLEAN_GRACE_MINUTES` en `PhaseOptions` + tests.** La precedencia minutos-sobre-horas y
   los tres casos nuevos en `PhaseOptionsTests.cs` (ausente, positiva y con precedencia, no
   numérica/≤0 ⇒ cae a horas).

4. **`docker/docker-compose.e2e.yml`.** El overlay con `extra_hosts`, `TARGET_TEMPORAL_HOST`,
   `PHASE_CLEAN_GRACE_MINUTES` y `DISCOVERY_LOOKBACK_DAYS` acotado. Verificar que
   `docker compose -f docker/docker-compose.yml -f docker/docker-compose.e2e.yml config` resuelve
   sin errores antes de levantar nada.

5. **`scripts/e2e/seed-orders.ps1` y `drain-orders.ps1`.** Contra la API real de `ReleaseOrderDemo`
   en `localhost:5000`. Probar cada uno de forma aislada: `seed-orders.ps1 -Count 1` deja una orden
   en `Waiting for release decision` (verificable con `GET /orders/{id}/status`); `drain-orders.ps1`
   sobre esa misma orden la cierra en `Completed`.

6. **`scripts/e2e/snapshot.ps1` + `docs/e2e/evidence/`.** Contra `MonitorApi` en `localhost:5100`.
   Probar con el stack del monitor solo (sin `ReleaseOrderDemo` corriendo todavía): `GET /patches`
   vuelve con `Count: 0`, el trigger no falla.

7. **`docs/e2e/releaseorder-patch-phases.md`.** Los tres diffs y el comando de rebuild, confirmados
   letra por letra contra `specs/04-patching-versionado-drenaje.md`,
   `specs/05-deprecacion-patch-audit-before-decision.md` y
   `specs/06-limpieza-patch-audit-before-decision.md` del repo `ReleaseOrderDemo` real (no
   reescribirlos de memoria).

8. **Línea base end-to-end.** Con ambos stacks arriba (`ReleaseOrderDemo` con `release-orden-worker`
   en su código limpio actual, monitor con el overlay de e2e) y el volumen `temporal_data` del
   monitor limpio: `snapshot.ps1 -Label 00-baseline` confirma `Count: 0` en `/patches`. Este paso
   establece que el Schedule del monitor arranca limpio antes de tocar código en `ReleaseOrderDemo`.

9. **Fases 1 y 2, con sus gates.** Aplicar el diff de fase 1 de `docs/e2e/releaseorder-patch-phases.md`,
   rebuild + redeploy de `release-orden-worker`, `seed-orders.ps1 -Count 2` (quedan pre-patch,
   arrancadas _antes_ del deploy — usar las de la línea base o crear nuevas antes de aplicar el
   diff), snapshot; drenarlas con `drain-orders.ps1`, snapshot; aplicar fase 2, rebuild, redeploy,
   `seed-orders.ps1 -Count 1`, snapshot; drenar, snapshot. Cada snapshot va a
   `docs/e2e/evidence/`.

10. **Fase 3 y verificación end-to-end manual.** Aplicar el diff de fase 3, rebuild, redeploy,
    `seed-orders.ps1 -Count 1`, esperar `PHASE_CLEAN_GRACE_MINUTES`, snapshot final. Contrastar la
    secuencia completa de `docs/e2e/evidence/*.json` contra la tabla de "Recorrido esperado" de este
    spec, confirmar en `docker compose logs patch-monitor-worker` (del stack del monitor) exactamente
    dos líneas de notificación (una por cada cambio de veredicto real: gate 1→2 y gate 2→3 abriendo),
    y confirmar en la UI de Temporal del monitor (`localhost:8234`) que las ejecuciones de
    `MonitorWorkflow` vienen cada 5 minutos sin gaps ni superposición. Pegar los resultados de estos
    comandos en esta spec (siguiendo el formato que ya usa `specs/08-control-api.md`, sección "##
    Plan de implementación", paso 10) antes de marcar los criterios de aceptación.

## Criterios de aceptación

- [ ] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [ ] `dotnet test PatchMonitor.sln` pasa con el stack de Docker **apagado**, incluidos los tests
      nuevos de `TargetHost` y `PHASE_CLEAN_GRACE_MINUTES`.
- [ ] `docker compose -f docker/docker-compose.yml -f docker/docker-compose.e2e.yml config` resuelve
      sin errores y no modifica ningún valor del compose base que no sea `patch-monitor-worker` y
      `monitor-api`.
- [ ] Con el stack de `ReleaseOrderDemo` en su código limpio actual, `GET /patches` del monitor
      devuelve `Count: 0` (línea base, paso 8).
- [ ] Tras desplegar la fase 1 del patch y con ejecuciones pre-patch abiertas, `GET /patches/...`
      reporta `Phase: Coexistence`, gate `Blocked` y al menos una ejecución bloqueante listada.
- [ ] Tras drenar las ejecuciones pre-patch, el mismo endpoint reporta gate `Open` y
      `NextPhase: Deprecated`, sin reiniciar el monitor.
- [ ] Tras desplegar la fase 2 y con ejecuciones con marker abiertas, `Phase: Deprecated` y gate
      `Blocked`.
- [ ] Tras drenarlas, gate `Open` y `NextPhase: Clean`.
- [ ] Tras desplegar la fase 3 y pasado `PHASE_CLEAN_GRACE_MINUTES`, `Phase: Clean`, `Source:
    Inferred`, sin override manual de por medio.
- [ ] `docker compose logs patch-monitor-worker` del stack del monitor muestra **exactamente dos**
      notificaciones de cambio de veredicto en todo el recorrido (una por cada apertura de gate), no
      una por corrida del Schedule.
- [ ] La UI de Temporal del monitor (`localhost:8234`) muestra ejecuciones de `MonitorWorkflow`
      espaciadas ~5 minutos, sin ningún bucle de polling visible en los logs del worker entre ticks.
- [ ] El estado del monitor (entity workflows, registry) vive en su propio cluster (`localhost:7234`,
      volumen `temporal_data` de `patchmonitor`) y en ningún momento se escribió sobre el cluster de
      `ReleaseOrderDemo`.
- [ ] Los tres diffs de `docs/e2e/releaseorder-patch-phases.md` coinciden letra por letra con los que
      documentan los specs 04/05/06 del repo `ReleaseOrderDemo` real.
- [ ] Los cuatro archivos de evidencia (`docs/e2e/evidence/*.json`) mínimos — línea base, fase 1
      bloqueada, ambos gates abiertos, fase 3 — existen y están referenciados desde el paso 10 de
      esta spec.

## Decisiones tomadas y descartadas

- **Sí:** reintroducir el patch `audit-before-decision` en el `ReleaseOrderDemo` real, en vez de un
  target sintético dentro de este repo. Es lo que `Construction.md` §7 pide literalmente ("apuntar
  al `ReleaseOrderDemo` real") y lo único que ejercita el `WorkflowType` y el namespace reales que un
  operador va a monitorear en producción.
- **No:** reusar o reconstruir `ProbeWorkflow`. Nunca fue código versionado, no es el proyecto de
  referencia que el README cita, y hubiera vuelto a dejar la validación real sin repetibilidad.
- **Sí:** `TARGET_TEMPORAL_HOST` dentro de `DiscoveryOptions`, no una clase `TargetOptions` nueva.
  Ya convive con `Namespace`, que es la otra mitad de la misma conexión; separarlas partiría en dos
  algo que siempre se configura junto.
- **No:** una clase de opciones separada para el target. Hubiera duplicado el patrón
  `FromEnvironment()` sin necesidad; `DiscoveryOptions` es exactamente "cómo conectarse y con qué
  topes al cluster observado", y `TargetHost` es parte de "cómo conectarse".
- **Sí:** `host.docker.internal:7233` para la conectividad entre stacks, en vez de red compartida.
  Cero cambios en `ReleaseOrderDemo`, sin colisión de DNS (`temporal` existe en ambos stacks), y sin
  acoplar el arranque de un stack al otro.
- **No:** declarar una red `external: true` apuntando a la red de `ReleaseOrderDemo`. Exigiría un
  alias de DNS para el hostname `temporal` colisionado y que `ReleaseOrderDemo` esté arriba antes de
  poder levantar el monitor — acoplamiento que `host.docker.internal` evita.
- **Sí:** `docker-compose.e2e.yml` como overlay separado, no modificar `docker-compose.yml` base. El
  compose base sigue siendo "el monitor apuntado a sí mismo" (caso por defecto, spec 01); el overlay
  es explícitamente la configuración de esta validación puntual.
- **Sí:** `PHASE_CLEAN_GRACE_MINUTES` con precedencia sobre `PHASE_CLEAN_GRACE_HOURS`, sin tocar el
  default de producción (24h). Permite acortar la espera de fase 3 solo cuando se declara
  explícitamente, sin abrir una puerta a que alguien deje un grace de minutos en producción por
  accidente de copiar el overlay.
- **No:** bajar `MONITOR_INTERVAL_MINUTES` para acelerar la corrida. El README exige 5 minutos como
  cadencia y este spec existe para validar el sistema tal como se va a operar; se fuerza el tick
  on-demand con `POST /schedule/trigger` (spec 08) en vez de cambiar el reloj.
- **Sí:** `DISCOVERY_LOOKBACK_DAYS=1` en el overlay de e2e. El descubrimiento no filtra por
  `WorkflowType` (decisión cerrada del spec 03) y el namespace `default` de `ReleaseOrderDemo` puede
  tener ejecuciones viejas de otros workflows; acotar la ventana reduce ruido sin reabrir esa
  decisión.
- **No:** automatizar el redeploy de las tres fases con un script que edite el código de
  `ReleaseOrderDemo`. Escondería exactamente el punto que `Construction.md` §2 remarca: el cambio de
  fase de un patch es una acción humana sobre código fuente, fuera del alcance de un programa. Los
  scripts de este spec solo generan y drenan ejecuciones — nunca tocan el código del proyecto
  observado.
- **Sí:** `scripts/e2e/` en PowerShell. Es el shell nativo del entorno de desarrollo (Windows) y el
  repo no tenía ningún script previo que fijara una convención distinta.

## Riesgos identificados

| Riesgo                                                                                                                                                                                                                              | Mitigación                                                                                                                                                                                                                                           |
| ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `host.docker.internal` no resuelve en el entorno de ejecución (algunos setups de Docker en Linux sin el soporte `host-gateway`).                                                                                                    | `extra_hosts: host.docker.internal:host-gateway` es soporte nativo desde Docker 20.10+/Compose reciente; si falla, el paso 4 lo detecta con `docker compose config` antes de levantar nada, y el fallback documentado es la IP del gateway del host. |
| El descubrimiento sin filtro de `WorkflowType` trae ejecuciones de `ShippingWorkflow`, `CrearOrdenWorkflow` u `OrderReportWorkflow` del mismo namespace, ensuciando el `GET /patches` con entradas irrelevantes.                    | Es la decisión cerrada del spec 03 (genérico a propósito); se acota con `DISCOVERY_LOOKBACK_DAYS=1` y el paso 10 filtra la evidencia por `WorkflowType: ReleaseOrderWorkflow` al verificar.                                                          |
| El `Schedule` del monitor, si ya existe de una corrida anterior en el mismo volumen `temporal_data`, no adopta la config nueva (`EnsureScheduleAsync` es create-if-absent).                                                         | El paso 8 exige partir de un volumen `temporal_data` limpio (`docker compose down -v` del stack del monitor) antes de la línea base.                                                                                                                 |
| Reintroducir el patch deja la rama de `ReleaseOrderDemo` con `ReleaseOrderPatchingTests` en rojo a propósito mientras dura la validación.                                                                                           | Documentado explícitamente en este spec; es temporal y se revierte (fase 3 = código limpio = el estado original) al cerrar el recorrido.                                                                                                             |
| El stack de `ReleaseOrderDemo` incluye SQL Server 2022, pesado en RAM; puede no arrancar junto al stack del monitor en una máquina ajustada.                                                                                        | Se verifica en el paso 8 antes de tocar código; si falla, es una restricción de la máquina de desarrollo, no del diseño del monitor, y se documenta como bloqueo del paso 10 hasta correr en una máquina con más recursos.                           |
| Los timers de `ReleaseOrderWorkflow` (`Workflow.DelayAsync(5s)` y `(10s)`) hacen que drenar una orden tarde ~15-20s de reloj real; con varias órdenes en paralelo el drenaje puede no estar completo cuando se dispara el snapshot. | `drain-orders.ps1` espera confirmación de `GetStatus == Completed` (o `Failed`/compensado) antes de devolver el control, y el paso 9 dispara el snapshot recién después de que el script termina.                                                    |

## Qué NO entra en este spec

- Monitorear varios namespaces o clusters en una misma corrida.
- Filtrar el descubrimiento por `WorkflowType`.
- Compartir la red de Docker entre el monitor y `ReleaseOrderDemo`.
- Commitear los diffs de fase en el repo de `ReleaseOrderDemo`.
- Automatizar el redeploy de las tres fases del patch con un script.
- Tests de integración con Docker dentro de `dotnet test`.
- Aplicar `Workflow.Patched` / `DeprecatePatch` sobre el propio `MonitorWorkflow`.

Cada uno, si entra, va en su propio spec.
