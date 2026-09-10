# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Idioma

Trabajar **siempre en español**: respuestas del chat, specs, mensajes de commit y documentación.
La única excepción es el encabezado obligatorio en inglés al inicio de este archivo.

**En el código la convención es inglés**: nombres de clases, métodos, variables, interfaces,
namespaces, nombres de proyectos, ramas de git, identificadores de workflows/activities/task queues
y comentarios dentro del código van en inglés. El español queda para la prosa: specs, README,
mensajes de commit y explicaciones.

## Estado actual

Proyecto nuevo (greenfield). El repo solo contiene `README.md` — todavía no hay solución, `src/`,
`specs/` ni historial de git. El README es el enunciado del problema (en español); todo lo de abajo
se deriva de él y del proyecto de referencia al que apunta.

## Qué se busca construir

Un **monitor genérico del estado de un workflow** para el ciclo de vida de versiones de un patch/
release: un programa que observa en qué estado está un patch, detecta cuándo puede avanzar al
siguiente estado y además puede **ejecutar** la transición (pasos 1→2, 2→3, …) en el orden correcto.
Dos responsabilidades que se mantienen separables: el monitor, y un componente general que dispara
los cambios del workflow.

Requisitos firmes del README:

- **C# / .NET**, respetando SOLID, inversión de dependencias y patrones de diseño.
- Usar **Temporal** para orquestación y scheduling si encaja; si no, construir el monitor con lo
  que ya provee el proyecto de referencia.
- **No hacer polling constante.** Usar un **Temporal Schedule**, preferentemente cada **5 minutos**.
- Usar **Docker** para BD / backend / servicios de apoyo, igual que en el proyecto de referencia.
- Igualar las **versiones de Temporal y .NET** del proyecto de referencia (ver abajo).

## Proyecto de referencia — igualar su stack y convenciones

Fuente de la verdad para los datos de dominio y para las versiones:
**https://github.com/Ramiro145/ReleaseOrderDemo** (rama `main`). Clonarlo/consultarlo para el
detalle exacto; el README lo cita para "documentación o datos" del dominio.

Ese proyecto (`ReleaseOrderDemo.sln`) es un demo de Temporal.io sobre **.NET 8** que referencia
**`Temporalio` 1.9.0**, con un stack de Docker Compose (Temporal auto-setup + Postgres + temporal-ui,
SQL Server 2022, API mínima de ASP.NET Core y procesos worker). Al crear este proyecto, replicar:

- **Layout multi-proyecto bajo `src/`**, todos `net8.0`:
  - `Contracts` — interfaces de workflow compartidas, DTOs, interfaces de repositorio/servicio.
    Todo lo demás depende de este; la forma del contrato de workflow la comparten cliente y worker.
  - `Common` — plumbing genérico de Temporal: un `WorkerHost` que conecta un `TemporalClient`,
    corre un `TemporalWorker`, resuelve las clases de activity desde DI por tipo concreto y hace
    drenaje ordenado ante SIGTERM / Ctrl+C (`CancellationTokenSource` enlazado, ~30s de
    `GracefulShutdownTimeout`); un `WorkflowStarter` para arranques one-shot; un `WorkflowValidator`
    (wrapper de `DescribeAsync`) para distinguir "not found" de otros errores de RPC.
  - Uno o más proyectos **worker** con las clases `[Workflow]` / `[Activity]`, cada Activity
    respaldada por una implementación en `Services/*`; las Activities se registran en DI por tipo
    concreto y se pasan a `WorkerHost` como lista de tipos (no por interfaz).
  - Un proyecto **API** (API mínima de ASP.NET Core) como único punto de entrada HTTP, con un
    `TemporalClient` singleton (`TEMPORAL_HOST`, default `temporal:7233`).
- Patrón **SAGA + compensación** para transiciones multi-paso: apilar una compensación en un
  `Stack<Func<Task>>` tras cada paso reversible, deshacer en LIFO ante fallo.
- **El estado como marcador de idempotencia**: avanzar el status persistido y aplicar el efecto de
  dominio en una única transacción SQL por paso, leyendo primero con `UPDLOCK`, de modo que un
  reintento at-least-once encuentra el status ya pasado el paso y omite el efecto. Sin tabla de
  ledger de idempotencia aparte.
- **Semántica de reintentos**: no-retryable desde la Activity
  (`ApplicationFailureException(nonRetryable: true)`) o desde el lado del workflow
  (`RetryPolicy.NonRetryableErrorTypes`).
- **Ciclo de vida de versionado** si el código del workflow cambia con ejecuciones vivas:
  `Workflow.Patched` → `Workflow.DeprecatePatch` → eliminar, cada fase condicionada a que
  `temporal workflow list` no muestre ejecuciones viejas todavía reproducibles.
- Layout de Docker: carpeta `docker/` con un Dockerfile por servicio y `docker-compose.yml`; los
  servicios worker llevan un `stop_grace_period` mayor que el timeout de shutdown ordenado para que
  Docker no haga SIGKILL a mitad del drenaje.

Confirmar versiones/patrones exactos leyendo el `CLAUDE.md`, `README.md`,
`docker/docker-compose.yml` y `src/` del repo de referencia antes de hacer el scaffolding.

## Metodología: Spec-Driven Design

Este proyecto se construye con **Spec-Driven Design** vía las skills `/spec` y `/spec-impl`. No
saltar directo al código.

- `/spec <feature>` — aclara requisitos con bloques de preguntas y luego escribe una spec numerada
  en `specs/NN-slug.md`. En esta fase no se escribe código.
- `/spec-impl <NN-nombre-spec>` — solo después de que el estado de la spec diga **Aprobada**; crea y
  cambia a una rama de git `spec-NN-slug` (nombre en inglés) y luego implementa paso a paso con
  pausas para revisar diffs.
- Las specs se escriben en **español** (igual que el README y las `specs/` del proyecto de
  referencia). Numerar las specs de forma secuencial; leer las dos specs más recientes antes de
  escribir una nueva para mantener las convenciones.
- Un `specs/.spec-config.yml` con `AutoCreateBranch` controla si `/spec-impl` crea la rama
  automáticamente.
- Seguir las prácticas de https://github.com/Klerith/fernando-skills.

## Build / test / run (una vez que exista la solución)

Se espera que coincida con el proyecto de referencia — verificar contra los archivos reales antes
de confiar en estos comandos:

```powershell
dotnet build <Solucion>.sln
dotnet test <Solucion>.sln          # xUnit + entorno de test time-skipping de Temporalio; sin Docker/SQL

# stack completo, desde docker/
docker compose build --no-cache
docker compose up -d --force-recreate
docker compose logs --tail=100 <servicio-worker>
```

Los tests con time-skipping descargan el binario del test-server de Temporal en la primera corrida
(red una vez, luego queda cacheado en el perfil de usuario).
