# 12 - Un solo Temporal, dos namespaces

**Estado:** Implementado
**Depende de:** [09-multi-target-e2e-validation.md](09-multi-target-e2e-validation.md)
**Fecha:** 2026-09-21

**Objetivo:** Que el monitor se apoye en un cluster de Temporal existente en vez de levantar el suyo
propio, aislando su estado por **namespace** (`monitor`) del namespace que observa (`default`), en
lugar de por cluster.

## Por qué existe este spec

`CLAUDE.md` y `Construction.md` §5 fijan la arquitectura vigente como "dos clusters, nunca mezclar":
uno propio del monitor (`TEMPORAL_HOST`, donde vive su estado) y otro observado
(`TARGET_TEMPORAL_HOST` + `TARGET_TEMPORAL_NAMESPACE`, de solo lectura). El `docker-compose.yml` del
spec 01 materializa eso levantando un Temporal completo propio (`temporal`, `temporal-db`,
`temporal-ui`) con los puertos corridos a 7234/8234/5433 exactamente para poder convivir en la misma
máquina que el cluster del proyecto observado (spec 09).

El requisito cambió: no debe existir un segundo Temporal solo para el monitor. El monitor tiene que
montarse sobre el Temporal que ya existe (el de `ReleaseOrderDemo`, como ejemplo de referencia, u
otro cualquiera) y aislarse ahí por namespace, no por cluster. Es una separación que Temporal ya
ofrece de forma nativa: dos namespaces en el mismo cluster no comparten Event History, Visibility ni
Search Attributes entre sí, que es justamente lo que hoy exige la separación completa de clusters.

De paso, esto corrige un defecto real que hoy está latente y sin ningún error visible: con el
compose base, `TEMPORAL_HOST` y `TARGET_TEMPORAL_HOST` valen ambos `temporal:7233`, y como ningún
cliente propio del monitor fija `Namespace` explícito, cae en `default` — el mismo namespace que
`TARGET_TEMPORAL_NAMESPACE` observa por default. El monitor se descubre a sí mismo
(`patch-state::*`, `patch-registry`, `MonitorWorkflow`) como ruido en su propio barrido, en
silencio. Separar por namespace con un default distinto (`monitor` vs. `default`) elimina esa
coincidencia por diseño.

La capacidad de observar un cluster remoto ajeno (el objetivo central validado en el spec 09) no se
toca: `TARGET_TEMPORAL_HOST` se mantiene tal cual. Lo que desaparece es la obligación de que el
monitor tenga _su propio_ cluster para poder correr.

## Alcance

**Incluye:**

- **`src/Contracts/Monitor/MonitorClusterOptions.cs`** (nuevo) — `Host` y `Namespace` de la conexión
  **propia** del monitor, leídos de `TEMPORAL_HOST` (default `temporal:7233`) y `TEMPORAL_NAMESPACE`
  (default `monitor`, ya no `default`), con el mismo patrón que
  `src/Contracts/Discovery/DiscoveryOptions.cs`: `FromEnvironment()` nunca lanza, constantes
  `Default*`, valor ausente/blanco cae al default.

- Los 4 sitios que hoy leen `TEMPORAL_HOST` suelto y nunca fijan `Namespace`, migrados a
  `MonitorClusterOptions`:
  - `src/Common/Temporal/WorkerHost.cs` (`ConnectAsync` privado, líneas 79-87)
  - `src/Common/Temporal/WorkflowStarter.cs` (las dos sobrecargas de `StartAsync`, líneas 18-19 y
    40-41)
  - `src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs` (el `Lazy<Task<ITemporalClient>>`
    del estado, líneas 49-53)
  - `src/MonitorApi/Program.cs` (el `TemporalClient` singleton, líneas 20-23)

- **`src/Common/Temporal/NamespaceBootstrapper.cs`** (nuevo) — crea el namespace propio si no existe,
  idempotente, mismo contrato que `ScheduleBootstrapper.EnsureScheduleAsync`
  (`src/Common/Temporal/ScheduleBootstrapper.cs:46`). Llamado desde `src/PatchMonitor/Program.cs` y
  `src/MonitorApi/Program.cs` antes de conectar.

- Guarda de arranque: si `Host`/`Namespace` propios coinciden exactamente con
  `DiscoveryOptions.TargetHost`/`Namespace`, el worker loguea una advertencia (nunca excepción,
  convención del repo) de que el monitor se va a observar a sí mismo.

- **`docker/docker-compose.yml`**: `temporal`, `temporal-db`, `temporal-ui` pasan detrás de
  `profiles: ["standalone"]` (dejan de levantarse con `docker compose up -d` por default, siguen
  disponibles con `--profile standalone`). `patch-monitor-worker` y `monitor-api` pasan a apuntar a
  `host.docker.internal:7233` tanto para `TEMPORAL_HOST` como para `TARGET_TEMPORAL_HOST`, con
  `TEMPORAL_NAMESPACE=monitor` y `TARGET_TEMPORAL_NAMESPACE=default`, más `extra_hosts:
host.docker.internal:host-gateway`. Se quita el `depends_on: condition: service_healthy` sobre
  `temporal` (ya no arranca en el mismo perfil) y se reemplaza por el reintento acotado del
  `NamespaceBootstrapper`.

- **`docker/docker-compose.e2e.yml`**: se simplifica a lo que de verdad lo distingue ahora
  (`PHASE_CLEAN_GRACE_MINUTES`, `DISCOVERY_LOOKBACK_DAYS` más cortos) y se corrige la asimetría
  actual, donde `monitor-api` recibe `TARGET_TEMPORAL_HOST` pero no `extra_hosts` ni
  `TARGET_TEMPORAL_NAMESPACE` (líneas 17-19).

- `GET /health` de `MonitorApi` (`src/MonitorApi/Program.cs:54-65`) agrega `monitorNamespace` junto
  al `targetNamespace` que ya expone.

- `web/src/api/types.ts` (espejo manual del DTO de `/health`) y su uso en
  `web/src/routes/Dashboard.tsx` / `web/src/components/TopBar.tsx` reflejan el campo nuevo.

- `CLAUDE.md`, `README.md`, `Construction.md` actualizados: la sección "Dos clusters, nunca mezclar"
  pasa a describir un cluster con dos namespaces; tabla de puertos, requisitos, variables de entorno
  y la sección "Apuntarlo a tu proyecto" del README.

**No incluye (fuera de alcance de este spec):**

- Tocar `ReleaseOrderDemo`: su `Common/` conecta siempre a `default` sin soporte de
  `TEMPORAL_NAMESPACE`. Por eso el demo se queda en `default` y solo el monitor se mueve a `monitor`.
- Monitorear varios namespaces en una misma corrida. Sigue siendo una instancia por namespace
  observado — `patch-registry` es un `WorkflowId` fijo sin discriminante
  (`src/Contracts/State/StateOptions.cs:23`) y `PatchRegistryWorkflow` no está pensado para
  particionarse.
- El spec 10 (auto-versionado del `MonitorWorkflow`), que sigue diferido.
- Migrar datos de un stack existente con historial en el namespace `default` propio: quien ya corría
  el monitor viejo empieza de cero en el namespace `monitor` nuevo (mismo criterio que "si cambiás la
  configuración del Schedule, `docker compose down -v`" ya documentado).

## Modelo de datos

Sin tipos de dominio nuevos. Una sola clase de configuración nueva:

```csharp
// src/Contracts/Monitor/MonitorClusterOptions.cs
public sealed record MonitorClusterOptions(string Host, string Namespace)
{
    public const string DefaultHost = "temporal:7233";
    public const string DefaultNamespace = "monitor";

    public static MonitorClusterOptions FromEnvironment()
    {
        // TEMPORAL_HOST, TEMPORAL_NAMESPACE — ausente/blanco ⇒ default, nunca lanza.
    }
}
```

`NamespaceBootstrapper` no introduce un tipo de estado propio: opera directo sobre
`ITemporalClient.WorkflowService` (`DescribeNamespaceAsync` / `RegisterNamespaceAsync`), igual que
`ScheduleBootstrapper` opera sobre `client.CreateScheduleAsync`.

## Plan de implementación

1. **`MonitorClusterOptions` + tests.** Nuevo archivo en `src/Contracts/Monitor/`, calcado de
   `DiscoveryOptions.cs`. Test espejo `test/PatchMonitor.Tests/Monitor/MonitorClusterOptionsTests.cs`
   con el mismo patrón de guardar/restaurar variables de proceso que
   `test/PatchMonitor.Tests/Discovery/DiscoveryOptionsTests.cs`. `dotnet test` en verde, sin tocar
   nada más todavía.

2. **Migrar `WorkerHost` y `WorkflowStarter` a `MonitorClusterOptions`.** Reemplazar el
   `Environment.GetEnvironmentVariable("TEMPORAL_HOST")` suelto de los 3 sitios en `Common` por
   `MonitorClusterOptions.FromEnvironment()`, fijando `Namespace` en `TemporalClientConnectOptions`.
   Sin tests unitarios propios sobre estos dos archivos (conectan de verdad); verificar que
   `dotnet build` sigue en 0 errores.

3. **`NamespaceBootstrapper`.** `DescribeNamespaceAsync`; si `NamespaceNotFoundException`,
   `RegisterNamespaceAsync` con retención configurable (`MONITOR_NAMESPACE_RETENTION_DAYS`, default 7
   días, mismo patrón `PositiveIntOrDefault` que el resto de las `*Options`); si ya existe, no hace
   nada. Reintento acotado (hasta ~15 s) tras registrar, porque el cache del frontend de Temporal
   tarda unos segundos en propagar el namespace nuevo a `DescribeNamespace`/`ListNamespaces`.

4. **Enchufar el bootstrapper y `MonitorClusterOptions` en ambos `Program.cs`.**
   `src/PatchMonitor/Program.cs`: `NamespaceBootstrapper.EnsureNamespaceAsync` antes de
   `EnsureScheduleAsync`. `src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs`: registrar
   `MonitorClusterOptions` como singleton y usarlo en el `Lazy<Task<ITemporalClient>>` de la línea
   49-53. `src/MonitorApi/Program.cs`: mismo bootstrapper antes de construir el `TemporalClient`
   singleton, y `Namespace` fijado en esa conexión.

5. **Guarda de auto-observación.** En `src/PatchMonitor/Program.cs`, comparar
   `MonitorClusterOptions` contra `DiscoveryOptions` ya resueltas; si coinciden, un
   `Console.WriteLine` de advertencia antes de arrancar el worker.

6. **`GET /health` con `monitorNamespace`.** Agregar el campo en la respuesta de
   `src/MonitorApi/Program.cs:59-64`. Reflejarlo en `web/src/api/types.ts` y mostrarlo en
   `TopBar.tsx`/`Dashboard.tsx` (revisar antes el diff sin commitear que ya tiene `TopBar.tsx`).

7. **`docker-compose.yml`: perfil `standalone` + apuntado al cluster existente.** Mover `temporal`,
   `temporal-db`, `temporal-ui` detrás de `profiles: ["standalone"]`; actualizar variables de
   `patch-monitor-worker` y `monitor-api` como se describe en "Alcance"; quitar el
   `depends_on: service_healthy` que ya no aplica.

8. **`docker-compose.e2e.yml` simplificado y corregido.** Dejar solo `PHASE_CLEAN_GRACE_MINUTES` y
   `DISCOVERY_LOOKBACK_DAYS`; agregar a `monitor-api` el `extra_hosts` y `TARGET_TEMPORAL_NAMESPACE`
   que hoy le faltan.

9. **Documentación.** `CLAUDE.md` (sección de clusters + tabla de puertos), `README.md` (Requisitos,
   Levantarlo, Apuntarlo a tu proyecto, tabla de configuración, límites conocidos), `Construction.md`
   (fila del spec 12 en §7, entrada en §3 que reemplace el supuesto de dos clusters). Nota para
   actualizar la guía visual del artifact si describe puertos o arquitectura de dos clusters.

10. **Verificación end-to-end manual.** Ver `## Criterios de aceptación` para el detalle exacto:
    levantar `ReleaseOrderDemo` con su propio compose, levantar el monitor sin perfil `standalone`
    apuntado a ese mismo cluster, confirmar namespace `monitor` creado y aislado, disparar el
    Schedule y verificar que el monitor descubre el patch de `ReleaseOrderDemo` y no se descubre a sí
    mismo.

## Criterios de aceptación

- [x] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [x] `dotnet test PatchMonitor.sln` pasa con el stack de Docker apagado (268 + los nuevos casos de
      `MonitorClusterOptionsTests`). *(276/276 en verde, verificado con el stack de Docker apagado.)*
- [x] `cd web && npm run build` compila sin errores de TypeScript.
- [x] Con `ReleaseOrderDemo` levantado con su propio compose (Temporal en `:7233`) y, desde
      `docker/`, `docker compose up -d` **sin** `--profile standalone`: quedan arriba solo
      `patch-monitor-worker`, `monitor-api` y `monitor-web` — ningún Temporal, Postgres ni UI nuevos.
      *(Verificado contra `ReleaseOrderDemo` real: exactamente esos 3 servicios arriba.)*
- [x] La UI de Temporal del cluster existente (`:8233` en `ReleaseOrderDemo`) lista los namespaces
      `default` y `monitor`; `patch-state::*`, `patch-registry` y las corridas de `MonitorWorkflow`
      aparecen solo en `monitor`. *(Confirmado por `temporal workflow list`: namespace `monitor`
      tiene solo `patch-registry`, `patch-state::default::ReleaseOrderWorkflow::audit-before-decision`,
      el scheduler y `MonitorWorkflow`; namespace `default` tiene solo ejecuciones de
      `ReleaseOrderWorkflow`, cero auto-observación.)*
- [x] `curl http://localhost:5100/health` devuelve `monitorNamespace: "monitor"`,
      `targetNamespace: "default"` y `temporal: "ok"`. *(Verificado literal contra el cluster real.)*
- [x] `curl -X POST http://localhost:5100/schedule/trigger` seguido de
      `curl http://localhost:5100/patches` devuelve el patch real de `ReleaseOrderWorkflow` y ningún
      patch propio del monitor (confirma que se terminó la auto-observación silenciosa). *(Se
      reintrodujo temporalmente el patch `audit-before-decision` (fase 1, sin commitear en
      `ReleaseOrderDemo`) para generar el marker real; `/patches` devolvió el patch en fase
      Coexistence, `Blocked` por 2 ejecuciones pre-patch abiertas reales del cluster.)*
- [x] `docker compose --profile standalone up -d` sigue levantando un Temporal propio completo para
      quien no tenga cluster existente, sin cambios de comportamiento respecto al stack actual.
      *(Verificado: `temporal`, `temporal-db`, `temporal-ui` sanos, conviviendo con el stack apuntado
      al cluster de `ReleaseOrderDemo`.)*
- [x] El Dashboard (`:5101`) muestra `monitorNamespace` junto al namespace observado. *(Verificado
      por API: `curl http://localhost:5101/api/health` vía el proxy de nginx devuelve
      `monitorNamespace`; el binding en `TopBar.tsx` es directo — sin browser disponible en este
      entorno para captura visual.)*

## Decisiones tomadas y descartadas

- **Sí:** namespace `monitor` para el estado propio y `default` para lo observado, con `monitor`
  como nuevo default de `TEMPORAL_NAMESPACE`. Evita por diseño la coincidencia con el default
  histórico de `TARGET_TEMPORAL_NAMESPACE`, que es la causa raíz de la auto-observación silenciosa
  descrita en "Por qué existe este spec".
- **Sí:** no tocar `ReleaseOrderDemo`. Es un repo ajeno (`CLAUDE.md`); su `Common/` no soporta
  `TEMPORAL_NAMESPACE` y agregarlo excede el alcance de este spec.
- **Sí:** mantener `TARGET_TEMPORAL_HOST` para seguir permitiendo observar un cluster remoto. Es el
  objetivo central del README, validado en el spec 09; este spec cambia dónde vive el estado del
  monitor, no qué puede observar.
- **Sí:** perfil `standalone` en vez de borrar los servicios `temporal`/`temporal-db`/`temporal-ui`
  del compose. Sigue habiendo gente sin cluster propio a mano; el perfil los deja disponibles sin que
  sean el camino por default.
- **Sí:** `MonitorClusterOptions` como clase nueva en vez de extender `DiscoveryOptions` con un
  segundo namespace. Son conceptualmente distintas (una describe la conexión propia de escritura, la
  otra la observada de solo lectura) y ya hay precedente de esa separación
  (`specs/09-...md`, decisión de mantener `TargetHost` dentro de `DiscoveryOptions` en vez de una
  clase separada, es el caso simétrico).
- **No:** migrar datos del estado ya escrito en el namespace `default` propio hacia `monitor`. Nadie
  tiene ejecuciones vivas en un stack productivo todavía (spec 09 fue el único e2e real); no vale la
  pena la complejidad de una migración para cero usuarios reales.
- **No:** crear un `docker-compose.standalone.yml` separado. El `profiles` de Compose alcanza sin
  duplicar definiciones de servicio.

## Riesgos identificados

| Riesgo                                                                                                                                               | Mitigación                                                                                                                                                   |
| ---------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| El namespace recién registrado no está listo de inmediato para `DescribeNamespace`/conexiones (cache del frontend de Temporal)                       | Reintento acotado (~15 s) en `NamespaceBootstrapper` antes de que el worker/API sigan                                                                        |
| Quitar `depends_on: service_healthy` sobre `temporal` dentro del mismo compose dificulta saber si el cluster observado está arriba antes de conectar | El `NamespaceBootstrapper` y la conexión ya reintentan; documentar en README que `host.docker.internal:7233` debe estar sano antes de `docker compose up -d` |
| Alguien deja `TEMPORAL_NAMESPACE` sin setear y por error coincide con `TARGET_TEMPORAL_NAMESPACE` en un cluster ajeno                                | La guarda de arranque (paso 5) advierte en el log; no bloquea porque la convención del repo es que la configuración nunca lanza                              |
| El perfil `standalone` queda sin ejercitarse una vez que el camino normal es el cluster existente, y se rompe en silencio con el tiempo              | Criterio de aceptación explícito que lo verifica en cada corrida de este spec                                                                                |
| Actualizar `CLAUDE.md`/`README.md`/`Construction.md`/guía visual y olvidar alguno deja documentación contradictoria sobre "dos clusters"             | Paso 9 los lista a los cuatro explícitamente; el criterio de "listo" de `Construction.md` §8 ya exige coherencia entre código y docs                         |

## Qué NO entra en este spec

- Auto-versionado del `MonitorWorkflow` (spec 10, diferido).
- Migración de datos de un stack existente al namespace `monitor` nuevo.
- Monitorear varios namespaces observados en una misma corrida/instancia.
- Cambios en `ReleaseOrderDemo` para que soporte `TEMPORAL_NAMESPACE`.

Cada uno, si entra, va en su propio spec.
