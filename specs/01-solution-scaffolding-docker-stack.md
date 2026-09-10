# 01 - Scaffolding de la solución y stack de Docker

**Estado:** Aprobado
**Depende de:** -
**Fecha:** 2026-09-10

**Objetivo:** Entregar la solución `.NET 8` multi-proyecto `PatchMonitor.sln` —`Contracts`, `Common`,
`PatchMonitor` (worker) y `MonitorApi`— que compila, con el `Common/` de Temporal portado del repo de
referencia y un stack de Docker Compose (Temporal auto-setup + Postgres + UI + worker + API, sin SQL
Server) que arranca, expone `GET /health` y ejecuta un `HealthWorkflow` trivial de punta a punta.

## Por qué existe este spec

Ningún spec de la hoja de ruta (02 a 10) es verificable sin un `docker compose up` que levante
Temporal, la UI y un worker que loguee. El modelo de dominio del spec 02 se testea sin Docker, pero
del 03 en adelante todo corre contra un cluster de Temporal real o contra el entorno de time-skipping
del SDK, y ambos necesitan que la solución exista, compile y tenga la forma multi-proyecto que el
resto de los specs asume. Este spec es esa base: cuatro proyectos vacíos que compilan y un stack que
arranca.

El `Construction.md` (§3, §4 restricción #6) fija que `Common/` **se reutiliza casi tal cual** del
repo de referencia `ReleaseOrderDemo`: `WorkerHost` (drenaje ante SIGTERM y Ctrl+C,
`GracefulShutdownTimeout` de 30 s, activities resueltas por tipo concreto desde DI), `WorkflowStarter`
y `WorkflowValidator` (wrapper de `DescribeAsync` que distingue "not found" de otros errores de RPC).
Copiar, no reinventar: este spec porta esos tres archivos sin cambios de lógica y deja
`ScheduleBootstrapper` —el único agregado nuevo a `Common`— para el spec 06.

La **ausencia de SQL Server** en el `docker-compose.yml` es la consecuencia visible de la decisión de
persistencia ya cerrada en el `Construction.md` (§3): el estado del monitor vive en entity workflows
de Temporal, sin BD propia. El stack de referencia arranca `mcr.microsoft.com/mssql/server` y un
`db-init`; acá esos dos servicios no existen, y ningún `.csproj` referencia `Microsoft.Data.SqlClient`
ni `System.Data.SqlClient`.

Los **puertos van desplazados** respecto del repo de referencia (Temporal 7234 en vez de 7233, UI
8234, Postgres 5433, API 5100). El spec 09 exige apuntar el monitor al `ReleaseOrderDemo` real con
**los dos stacks de Docker arriba a la vez** en la misma máquina; si ambos publicaran 7233 y 8233
habría colisión de puertos y de nombres de contenedor. Resolverlo desde el spec 01 evita un refactor
del compose más adelante.

Por la misma razón el monitor separa desde el día uno **dónde corre** (`TEMPORAL_HOST`) de **a quién
observa** (`TARGET_TEMPORAL_HOST` / `TARGET_TEMPORAL_NAMESPACE`). En este spec ambos apuntan al mismo
cluster —el del compose— y el segundo par todavía no se consume, pero dejar la variable declarada y
documentada ahora evita tocar la configuración de todos los servicios cuando el spec 03 empiece a
leer de un cluster destino.

## Alcance

**Incluye:**

- **`PatchMonitor.sln`** en la raíz, con los cuatro proyectos de `src/` referenciados.
- **`.gitignore`** y **`.dockerignore`** en la raíz, copiados del repo de referencia (`obj/`, `bin/`,
  `.vs/`, `*.user`; el `.dockerignore` además excluye `.git/`, `docker-compose.override.yml`,
  `Dockerfile.*`). El `git init` y el primer commit los hace la persona antes de correr `/spec-impl`;
  este spec solo aporta los dos archivos de ignore.
- **`src/Contracts/Contracts.csproj`** — `net8.0`, `ImplicitUsings` y `Nullable` habilitados,
  `PackageReference` a `Temporalio` 1.9.0 y **nada más** (sin `Microsoft.Data.SqlClient`: no hay BD
  propia). Contenido:
  - `src/Contracts/Workflows/IHealthWorkflow.cs` — interfaz de workflow compartida
    cliente/worker (ver Modelo de datos).
  - `src/Contracts/TaskQueues.cs` — clase estática con las constantes de task queue
    (`PatchMonitor = "patch-monitor-task-queue"`).
- **`src/Common/Common.csproj`** — `net8.0`, `PackageReference` a `Temporalio` 1.9.0 y
  `Microsoft.Extensions.DependencyInjection` 8.0.0. Portados **sin cambios de lógica** desde
  `ReleaseOrderDemo/src/Common/Temporal/`:
  - `src/Common/Temporal/WorkerHost.cs` — `GracefulShutdownTimeout = 30s`,
    `RunAsync<TWorkflow>(taskQueue, serviceProvider, activityTypes, additionalWorkflowTypes)` con
    activities resueltas por `GetRequiredService(activityType)` + `AddAllActivities(type, instance)`,
    y `RunWithGracefulShutdownAsync` con el registro de `Console.CancelKeyPress` y
    `PosixSignalRegistration` para `SIGTERM` (tolerando `PlatformNotSupportedException` en Windows).
  - `src/Common/Temporal/WorkflowStarter.cs` — arranques one-shot con `workflowId` = prefijo + GUID.
  - `src/Common/Temporal/WorkflowValidator.cs` — wrapper de `DescribeAsync` que devuelve
    `(bool Exists, WorkflowExecutionDescription? Info, string? Error)` y distingue el
    `RpcException` con `"no rows in result set"` de cualquier otro error.
- **`src/PatchMonitor/PatchMonitor.csproj`** — `OutputType` `Exe`, `net8.0`, `PackageReference` a
  `Temporalio` 1.9.0 y `Microsoft.Extensions.DependencyInjection` 8.0.0; `ProjectReference` a
  `Contracts` y `Common`. Contenido:
  - `src/PatchMonitor/Workflows/HealthWorkflow.cs` — `[Workflow]` que implementa `IHealthWorkflow` y
    devuelve un string fijo (ver Modelo de datos). Sin activities.
  - `src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs` — `AddPatchMonitorServices` que
    por ahora registra un `ServiceCollection` vacío de servicios de dominio (placeholder para los
    specs 03+); existe para fijar el patrón de DI del repo de referencia.
  - `src/PatchMonitor/Program.cs` — lee `MONITOR_TASK_QUEUE` (default
    `patch-monitor-task-queue`), construye el `ServiceProvider`, y llama
    `WorkerHost.RunAsync<HealthWorkflow>(taskQueue, provider, activityTypes: Array.Empty<Type>())`.
- **`src/MonitorApi/MonitorApi.csproj`** — `Microsoft.NET.Sdk.Web`, `net8.0`; `PackageReference` a
  `Temporalio` 1.9.0, `Microsoft.AspNetCore.OpenApi` 8.0.8 y `Swashbuckle.AspNetCore` 10.0.1;
  `ProjectReference` a `Contracts` y `Common`. Contenido:
  - `src/MonitorApi/Program.cs` — API mínima. `TemporalClient` singleton que conecta a
    `TEMPORAL_HOST` (default `temporal:7233`). `builder.WebHost.UseUrls("http://0.0.0.0:5100")`.
    Swagger habilitado. Endpoints:
    - `GET /health` → `{ temporal: "ok" | "unreachable", targetNamespace, taskQueue }`, usando
      `client.Connection.WorkflowService` / un `DescribeNamespaceAsync` o el
      `WorkflowValidator` sobre un id inexistente para probar reachability sin fallar.
    - `POST /health/workflow` → arranca `HealthWorkflow` con `WorkflowStarter` y devuelve el
      `workflowId`.
- **`docker/Dockerfile.PatchMonitor`** — multi-stage `sdk:8.0` → `aspnet:8.0`, copiando primero los
  `.csproj` de `Common`, `Contracts` y `PatchMonitor` para cachear el `restore`, luego el resto;
  `ENTRYPOINT ["dotnet", "PatchMonitor.dll"]`. Misma forma que `Dockerfile.ReleaseOrder`.
- **`docker/Dockerfile.MonitorApi`** — análogo, `EXPOSE 5100`,
  `ENTRYPOINT ["dotnet", "MonitorApi.dll"]`. Misma forma que `Dockerfile.OrderApi`.
- **`docker/docker-compose.yml`** — servicios `temporal` (`temporalio/auto-setup:1.23.0`),
  `temporal-db` (`postgres:15` con healthcheck `pg_isready`), `temporal-ui`
  (`temporalio/ui:2.23.0`), `patch-monitor-worker` (`stop_grace_period: 45s`) y `monitor-api`.
  **Sin `db` (SQL Server) ni `db-init`.** Puertos según la tabla de Modelo de datos.
- **`docker/dynamicconfig/development.yaml`** — copiado del repo de referencia (habilita
  `frontend.enableUpdateWorkflowExecution`, necesario para los `[WorkflowUpdate]` del spec 08).

**No incluye (fuera de alcance de este spec):**

- **SQL Server, cualquier BD de negocio, o un `Microsoft.Data.SqlClient` en algún `.csproj`.** La
  decisión de persistencia (entity workflows) está cerrada en `Construction.md` §3.
- **Los puertos de dominio** `IPatchDiscovery`, `IPhaseResolver`, `INotifier`, `IDecisionSink` y sus
  DTOs: son de los specs 02 a 07.
- **`ScheduleBootstrapper` y el Temporal Schedule de 5 minutos:** spec 06. `Common` queda con los
  tres archivos portados y nada más.
- **`PatchStateWorkflow`, `PatchRegistryWorkflow`, `MonitorWorkflow`:** specs 05 y 06. El único
  `[Workflow]` de este spec es `HealthWorkflow`.
- **El proyecto `test/PatchMonitor.Tests`:** lo crea el spec 02 (dominio puro, unitarios sin Docker).
  Este spec no agrega proyecto de tests a la solución.
- **Frontend:** el `Construction.md` (§3) lo deja fuera de todo el proyecto.
- **Apuntar el monitor a `ReleaseOrderDemo`:** spec 09. Acá `TARGET_TEMPORAL_HOST` existe como
  variable con default al propio cluster, pero nada la consume todavía.
- **Consumir `TARGET_TEMPORAL_HOST` / `TARGET_TEMPORAL_NAMESPACE` para conectarse a un cluster
  distinto:** empieza en el spec 03.

## Modelo de datos

Este spec **no introduce esquema SQL** (no hay BD). Las estructuras nuevas son la solución en disco,
la interfaz de workflow de health-check, y la configuración por variables de entorno y puertos.

Árbol de carpetas objetivo tras este spec:

```text
proyecto_monitoreo/
├── PatchMonitor.sln
├── .gitignore
├── .dockerignore
├── README.md
├── CLAUDE.md
├── Construction.md
├── specs/
│   ├── .spec-config.yml
│   └── 01-solution-scaffolding-docker-stack.md
├── src/
│   ├── Contracts/
│   │   ├── Contracts.csproj
│   │   ├── TaskQueues.cs
│   │   └── Workflows/IHealthWorkflow.cs
│   ├── Common/
│   │   ├── Common.csproj
│   │   └── Temporal/
│   │       ├── WorkerHost.cs
│   │       ├── WorkflowStarter.cs
│   │       └── WorkflowValidator.cs
│   ├── PatchMonitor/
│   │   ├── PatchMonitor.csproj
│   │   ├── Program.cs
│   │   ├── Workflows/HealthWorkflow.cs
│   │   └── Infrastructure/ServiceCollectionExtensions.cs
│   └── MonitorApi/
│       ├── MonitorApi.csproj
│       └── Program.cs
└── docker/
    ├── Dockerfile.PatchMonitor
    ├── Dockerfile.MonitorApi
    ├── docker-compose.yml
    └── dynamicconfig/development.yaml
```

Interfaz de workflow compartida (`src/Contracts/Workflows/IHealthWorkflow.cs`):

```csharp
using Temporalio.Workflows;

namespace Contracts.Workflows;

[Workflow]
public interface IHealthWorkflow
{
    [WorkflowRun]
    Task<string> RunAsync();
}
```

`HealthWorkflow` (`src/PatchMonitor/Workflows/HealthWorkflow.cs`) devuelve la constante
`"patch-monitor alive"` sin ejecutar activities ni timers: es la prueba mínima de que el worker toma
tareas de la task queue y las completa.

Variables de entorno (todas con default; el stack de Docker las fija explícitamente):

| Variable                    | Default                    | Quién la lee                               | Para qué                                                                                |
| --------------------------- | -------------------------- | ------------------------------------------ | --------------------------------------------------------------------------------------- |
| `TEMPORAL_HOST`             | `temporal:7233`            | `WorkerHost`, `MonitorApi`                 | Dónde corre el cluster propio del monitor (conexión del worker y del cliente de la API) |
| `MONITOR_TASK_QUEUE`        | `patch-monitor-task-queue` | `PatchMonitor/Program.cs`, `MonitorApi`    | Task queue del worker del monitor                                                       |
| `TARGET_TEMPORAL_HOST`      | `temporal:7233`            | _(declarada, sin consumidor en este spec)_ | Cluster que el monitor **observa**; lo consume el spec 03                               |
| `TARGET_TEMPORAL_NAMESPACE` | `default`                  | _(declarada, sin consumidor en este spec)_ | Namespace observado; lo consume el spec 03                                              |

Puertos publicados por `docker/docker-compose.yml` (desplazados respecto de `ReleaseOrderDemo` para
poder tener los dos stacks arriba a la vez, requisito del spec 09):

| Servicio                 | Interno | Externo  | Por qué está desplazado                                |
| ------------------------ | ------- | -------- | ------------------------------------------------------ |
| `temporal`               | 7233    | **7234** | `ReleaseOrderDemo` publica 7233                        |
| `temporal-ui`            | 8080    | **8234** | `ReleaseOrderDemo` publica 8233                        |
| `temporal-db` (Postgres) | 5432    | **5433** | `ReleaseOrderDemo` publica 5432                        |
| `monitor-api`            | 5100    | **5100** | `ReleaseOrderDemo` publica 5000/5001; 5100 queda libre |

Nombre de proyecto de Docker Compose: `patchmonitor` (vía `name:` en el compose o `-p patchmonitor`),
para que los contenedores y la red no colisionen con los del repo de referencia.

## Plan de implementación

1. **Ignore files y solución vacía.** Crear `.gitignore` y `.dockerignore` (copiados del repo de
   referencia) y `PatchMonitor.sln` sin proyectos todavía. `dotnet sln list` responde sin error. El
   `git init` + primer commit lo hizo la persona antes de `/spec-impl`; este paso solo agrega los
   archivos.

2. **Proyecto `Contracts`.** Crear `src/Contracts/Contracts.csproj` (`net8.0`, `Temporalio` 1.9.0,
   sin SqlClient), `src/Contracts/Workflows/IHealthWorkflow.cs` y `src/Contracts/TaskQueues.cs`.
   `dotnet sln add`. Compila.

3. **Proyecto `Common` portado.** Crear `src/Common/Common.csproj` y copiar los tres archivos de
   `ReleaseOrderDemo/src/Common/Temporal/` **sin cambios de lógica** (solo ajustar `namespace` si el
   repo de referencia usa uno con nombre de demo; se mantiene `namespace Common`). Añadir a
   `WorkerHost.cs` un comentario recordando que `stop_grace_period` en el compose debe superar los
   30 s de `GracefulShutdownTimeout`. `dotnet sln add`. Compila.

4. **Proyecto `PatchMonitor` (worker).** Crear el `.csproj` (`Exe`, referencias a `Contracts` y
   `Common`), `Workflows/HealthWorkflow.cs`, `Infrastructure/ServiceCollectionExtensions.cs` y
   `Program.cs` que resuelve la task queue de `MONITOR_TASK_QUEUE` y llama
   `WorkerHost.RunAsync<HealthWorkflow>` con lista de activity types vacía. `dotnet sln add`.
   Compila; `dotnet run --project src/PatchMonitor` en local sin cluster falla al conectar pero
   loguea el intento (comportamiento esperado sin Docker).

5. **Proyecto `MonitorApi`.** Crear el `.csproj` (`Sdk.Web`), `Program.cs` con el `TemporalClient`
   singleton sobre `TEMPORAL_HOST`, `UseUrls("http://0.0.0.0:5100")`, Swagger, y los endpoints
   `GET /health` (reachability del cluster, reusando `WorkflowValidator` para no romper si el
   workflow no existe) y `POST /health/workflow` (arranca `HealthWorkflow` con `WorkflowStarter`).
   `dotnet sln add`. Compila.

6. **Build de la solución completa.** `dotnet build PatchMonitor.sln` termina con 0 errores y 0
   advertencias. Commit del scaffolding de código.

7. **Dockerfiles y dynamic config.** Crear `docker/Dockerfile.PatchMonitor` y
   `docker/Dockerfile.MonitorApi` (multi-stage, copiando primero los `.csproj` para cachear el
   `restore`) y `docker/dynamicconfig/development.yaml` (copiado del repo de referencia). Commit.

8. **`docker-compose.yml`.** Crear `docker/docker-compose.yml` con `name: patchmonitor` y los cinco
   servicios: `temporal` (`auto-setup:1.23.0`, `DYNAMIC_CONFIG_FILE_PATH` apuntando al volumen de
   `dynamicconfig`), `temporal-db` (`postgres:15`, healthcheck `pg_isready`, volumen nombrado),
   `temporal-ui` (`ui:2.23.0`), `patch-monitor-worker` (build con `Dockerfile.PatchMonitor`,
   `TEMPORAL_HOST=temporal:7233`, `TARGET_TEMPORAL_HOST=temporal:7233`,
   `TARGET_TEMPORAL_NAMESPACE=default`, `stop_grace_period: 45s`, `depends_on: temporal`) y
   `monitor-api` (build con `Dockerfile.MonitorApi`, `5100:5100`, mismas env vars, `depends_on:
temporal`). Sin SQL Server ni `db-init`. Commit.

9. **Verificación end-to-end manual.** Desde `docker/`:
   `docker compose build --no-cache` y `docker compose up -d`. Comprobar:
   - `docker compose ps` muestra los 5 servicios `Up` (y `temporal-db` `healthy`).
   - `docker compose logs patch-monitor-worker` contiene
     `Worker listening on 'patch-monitor-task-queue'...`.
   - `curl http://localhost:5100/health` responde `200` con `temporal: "ok"`.
   - La Temporal UI carga en `http://localhost:8234`.
   - `curl -X POST http://localhost:5100/health/workflow` devuelve un `workflowId`, y esa ejecución
     aparece en la UI en estado `Completed` con resultado `"patch-monitor alive"`.
   - `docker compose stop patch-monitor-worker` imprime el mensaje de drenaje
     (`Worker draining (SIGTERM received)...`) y luego `Worker stopped cleanly.` en menos de 45 s.
   - Con un stack de `ReleaseOrderDemo` levantado en paralelo, `docker compose up -d` de este
     proyecto no reporta colisión de puertos ni de nombres de contenedor.
     Registrar la salida de estos comandos en este spec antes de marcar los criterios de aceptación.

## Criterios de aceptación

- [ ] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [ ] La solución tiene exactamente 4 proyectos (`Contracts`, `Common`, `PatchMonitor`,
      `MonitorApi`), todos `net8.0` y todos con `PackageReference Include="Temporalio" Version="1.9.0"`.
- [ ] `grep -ri "mssql\|SqlConnection\|Data.SqlClient" src/ docker/` no devuelve ninguna coincidencia.
- [ ] `src/Common/Temporal/` contiene `WorkerHost.cs`, `WorkflowStarter.cs` y `WorkflowValidator.cs`
      con la misma lógica que el repo de referencia (drenaje SIGTERM + Ctrl+C,
      `GracefulShutdownTimeout` de 30 s, `WorkflowValidator` distinguiendo `"no rows in result set"`).
      No hay `ScheduleBootstrapper`.
- [ ] `docker compose -p patchmonitor up -d` deja los 5 servicios `Up` y `temporal-db` `healthy`; no
      hay ningún servicio de SQL Server.
- [ ] La Temporal UI responde en `http://localhost:8234` y Temporal en `localhost:7234`.
- [ ] `GET http://localhost:5100/health` devuelve `200` con `temporal: "ok"`.
- [ ] `POST http://localhost:5100/health/workflow` crea una ejecución de `HealthWorkflow` que termina
      en `Completed` con resultado `"patch-monitor alive"`, visible en la UI.
- [ ] `docker compose stop patch-monitor-worker` produce el log de drenaje y `Worker stopped
    cleanly.` en menos de 45 s (drenaje ordenado verificado).
- [ ] Con un stack de `ReleaseOrderDemo` corriendo en paralelo, levantar este stack no colisiona en
      puertos ni en nombres de contenedor.

## Decisiones tomadas y descartadas

- **Sí:** portar `Common/` del repo de referencia sin cambios de lógica (solo namespace). Lo pide
  `Construction.md` §4 restricción #6 ("copiar, no reinventar"); el drenaje ordenado ya está probado
  ahí.
- **No:** reescribir `WorkerHost` "más limpio" o genérico. Ampliaría el alcance y arriesgaría el
  comportamiento de drenaje que specs posteriores (06, 10) dan por sentado.
- **Sí:** stack de Docker **sin SQL Server**. Es la consecuencia directa de la decisión de
  persistencia en entity workflows (`Construction.md` §3); mantener el servicio "por las dudas" sería
  infraestructura muerta.
- **No:** dejar el `db` / `db-init` del compose de referencia comentados. Ruido; el historial de git
  del repo de referencia ya conserva ese ejemplo.
- **Sí:** puertos desplazados (7234 / 8234 / 5433 / 5100) y nombre de proyecto compose `patchmonitor`.
  El spec 09 necesita los dos stacks arriba simultáneamente; resolverlo ahora evita un refactor del
  compose.
- **No:** puertos idénticos a `ReleaseOrderDemo`. Impediría la validación multi-target del spec 09
  sin apagar el otro stack.
- **Sí:** `HealthWorkflow` trivial + `GET /health` + `POST /health/workflow` como prueba de vida. Es
  el mínimo que ejercita worker + API + cluster de punta a punta y da un criterio de aceptación
  binario.
- **No:** un worker que solo loguea "listening" sin workflow propio. No probaría que una ejecución se
  completa; el primer spec que lo probaría sería el 06, demasiado tarde para detectar un problema de
  scaffolding.
- **Sí:** declarar `TARGET_TEMPORAL_HOST` / `TARGET_TEMPORAL_NAMESPACE` desde este spec, con default
  al propio cluster y sin consumidor todavía. Evita editar la config de todos los servicios cuando el
  spec 03 empiece a leer del cluster observado.
- **No:** una sola variable `TEMPORAL_HOST` como en el repo de referencia. Mezclaría "dónde corre el
  monitor" con "a quién observa", y esa separación es el corazón de lo genérico del proyecto.
- **Sí:** dejar el proyecto de tests para el spec 02. El 02 es dominio puro y testeable sin Docker;
  crear el `.csproj` de tests junto a su primer test mantiene cada spec autocontenido.
- **No:** crear `test/PatchMonitor.Tests` vacío en este spec. Un proyecto de test sin tests es andamio
  sin uso hasta el 02.

## Riesgos identificados

| Riesgo                                                                                                                                    | Mitigación                                                                                                                                                                                                                 |
| ----------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| El stack colisiona en puertos o nombres de contenedor con un `ReleaseOrderDemo` levantado en la misma máquina.                            | Puertos desplazados (tabla de Modelo de datos), `name: patchmonitor` en el compose, contenedores con prefijo propio. Criterio de aceptación explícito con los dos stacks arriba.                                           |
| `temporalio/auto-setup` tarda decenas de segundos en aceptar conexiones y la API o el worker arrancan antes → primeras conexiones fallan. | `depends_on: temporal` + el `TemporalClient.ConnectAsync` reintenta; `GET /health` reporta `unreachable` en vez de tirar 500 mientras el cluster inicia. La verificación del paso 9 se hace tras `docker compose ps` sano. |
| `PosixSignalRegistration` para `SIGTERM` no existe en Windows en `dotnet run` local.                                                      | `WorkerHost` ya captura `PlatformNotSupportedException` / `ArgumentException` y sigue con Ctrl+C; el drenaje por `SIGTERM` solo se exige dentro del contenedor.                                                            |
| `HealthWorkflow` queda como ruido en specs posteriores.                                                                                   | Documentado acá y en `Construction.md` §7 (spec 06): `MonitorWorkflow` lo reemplaza como workflow principal; `HealthWorkflow` puede quedar como sonda o eliminarse en el 06.                                               |
| El `restore` de Docker no cachea y cada build baja NuGet entero.                                                                          | Los Dockerfile copian primero los `.csproj` de `Common`, `Contracts` y el proyecto final, y corren `dotnet restore` antes de copiar el código, igual que los del repo de referencia.                                       |
| Versiones de imagen de Temporal (`auto-setup`, `ui`) distintas de las del repo de referencia introducen diferencias de comportamiento.    | Se fijan exactamente las de `Construction.md` §3: `auto-setup:1.23.0`, `ui:2.23.0`, `postgres:15`. Sin tags `latest`.                                                                                                      |

## Qué NO entra en este spec

- Cualquier BD (SQL Server, Postgres de negocio) o dependencia de SQL en un `.csproj`.
- Los puertos de dominio `IPatchDiscovery`, `IPhaseResolver`, `INotifier`, `IDecisionSink` y sus DTOs.
- `ScheduleBootstrapper`, el Temporal Schedule de 5 minutos y cualquier lógica de scheduling.
- `PatchStateWorkflow`, `PatchRegistryWorkflow`, `MonitorWorkflow` y las activities del monitor.
- El proyecto `test/PatchMonitor.Tests` y cualquier test automatizado.
- Consumir `TARGET_TEMPORAL_HOST` / `TARGET_TEMPORAL_NAMESPACE` para conectarse a un cluster distinto
  del propio.
- Apuntar el monitor al `ReleaseOrderDemo` real y reproducir el recorrido de fases.
- Frontend, notificador, API de control más allá de los dos endpoints de health.

Cada uno, si entra, va en su propio spec.
