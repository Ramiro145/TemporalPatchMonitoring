# 13 - Atribución de `Absent` por patch

**Estado:** Implementado
**Depende de:** [03-patch-discovery-two-tier.md](03-patch-discovery-two-tier.md), [04-current-phase-resolution.md](04-current-phase-resolution.md)
**Fecha:** 2026-09-24

**Objetivo:** que cada patch se evalúe con evidencia propia de ausencia de su marker, para que
llegue a fase Clean aunque su `workflowType` tenga otros patches activos, sin aumentar los falsos
Clean.

## Por qué existe este spec

`docs/limitacion-clean-patches-concurrentes.md` confirmó un bloqueo estructural: cuando un
`workflowType` tiene más de un patch activo a la vez, ninguno llega a fase Clean por inferencia
automática, aunque su código individual ya esté limpio.

La causa está en `PatchDiscoveryService.DiscoverAsync`
(`src/PatchMonitor/Services/PatchDiscoveryService.cs:34-80`): una ejecución sin ningún marker
`core_patch` en toda su historia entra al bucket `floating` y solo desde ahí se atribuye como
`Absent` a los patches existentes de ese `workflowType` (método `AttributeFloating`, líneas
130-146). Si cualquiera de esos patches sigue activo, ninguna ejecución nueva llega nunca "libre de
todo marker" → el bucket queda vacío → `Absent` nunca se calcula para ninguno, ni siquiera para el
que ya no tiene rastro en el código.

Se reprodujo en vivo contra `ssy-yardflow`: un patch de prueba (`patchmonitor-test-v1`) ya limpio
del código nunca llegó a Clean porque el workflow hereda de una clase base con un patch permanente
propio (`gate-cierre-terminal-v1`) que sigue emitiendo su marker en toda ejecución.

Al resolverlo se identificó un riesgo colateral ("falso Clean"): si `Workflow.Patched(id)` está
dentro de una rama que rara vez se ejecuta, las ejecuciones que no pasan por ahí quedan sin marker
y hoy ya podrían leerse como "código limpio" con solo una ejecución de evidencia. Al atribuir
`Absent` por patch individual este riesgo se vuelve más frecuente (antes, con el bucket `floating`
vacío en la práctica, casi nunca se llegaba a evaluar). Este spec resuelve el bloqueo estructural
**y** mitiga el falso Clean con dos capas descritas en Alcance.

## Alcance

**Incluye:**

- **`PatchDiscoveryService.DiscoverAsync`** (`src/PatchMonitor/Services/PatchDiscoveryService.cs`):
  reemplazar el bucket `floating` + `AttributeFloating` por atribución directa. Toda ejecución cuya
  historia se leyó completa (`historyReadable = true`) se atribuye como `MarkerPresence.Absent` a
  cada `PatchKey` **ya existente** de su `workflowType` para el cual esa ejecución no trajo marker
  propio (ni por atributo ni por historia). Toda ejecución con historia no legible se atribuye como
  `MarkerPresence.Unknown` a esos mismos `PatchKey` (fuerza `IsTruncated = true`). Se conserva:
  - `overwrite: false` al llamar `Put` — un marker propio (`Present`/`PresentDeprecated`) nunca se
    pisa con `Absent`/`Unknown` de la atribución.
  - La regla de no crear patches nuevos por ausencia: la atribución solo alcanza `PatchKey` que ya
    tengan al menos una ejecución con marker propio en este barrido.
  - Ejecuciones sin marcador de ningún patch conocido de su `workflowType` (el caso hoy cubierto por
    `floating` cuando `byKey` todavía no tiene ninguna entrada de ese tipo) simplemente no producen
    ningún `ExecutionSnapshot` — igual que hoy cuando `byKey.Count == 0`.

- **`PhaseResolver.Infer`, caso 3 (Clean)** (`src/PatchMonitor/Services/PhaseResolver.cs:68-85`),
  dos condiciones nuevas además de las actuales (ninguna ejecución abierta con marker + evidencia
  `Absent` posterior al `cutoff`):
  - **Capa 1 — no saltar fases:** `withMarker` debe contener al menos una ejecución
    `MarkerPresence.PresentDeprecated`. Un patch que nunca pasó por fase Deprecated no puede saltar
    directo a Clean.
  - **Capa 2 — evidencia mínima adaptativa:** sea `withEvidence` el subconjunto de `snaps` con
    `StartTime <= cutoff` y marker `Present`, `PresentDeprecated` o `Absent` (excluye `Unknown`).
    `p = |con marker| / |withEvidence|`. El N de ejecuciones `Absent` con `StartTime > cutoff`
    requerido es:
    - `p >= 1` (todas las ejecuciones previas traían el marker) ⇒ `N = 1`, igual que el
      comportamiento actual.
    - `0 < p < 1` ⇒ `N = max(1, ceil(ln(1 - confianza) / ln(1 - p)))`.
    - `p == 0` o `withEvidence` vacío ⇒ no hay evidencia de que el patch alguna vez se ejecutó con
      marker en la ventana; no se infiere Clean por esta vía (cae al caso 6, `Unknown`, como hoy).
    Si `count(Absent con StartTime > cutoff) < N`, el caso 3 no aplica: el patch se resuelve por el
    caso 4/5 (Deprecated/Coexistence según el marker más reciente) con un `Reason` que incluye
    cuántas ejecuciones limpias faltan y el `p`/`N` usados, p. ej.
    `"faltan 12 ejecuciones sin marker para confirmar Clean (p=0.10, N=29, vistas=17)"`.

- **`PhaseOptions`** (`src/Contracts/Phase/PhaseOptions.cs`): nuevo campo `double CleanConfidence`.
  `FromEnvironment()` lee `PHASE_CLEAN_GRACE_MINUTES`/`PHASE_CLEAN_GRACE_HOURS` como hoy, y además
  `PHASE_CLEAN_CONFIDENCE` (double, cultura invariante). Ausente, no numérico, o fuera del rango
  abierto `(0, 1)` ⇒ `DefaultCleanConfidence = 0.95`. Nunca lanza — mismo patrón que el resto de
  `*Options`.

- **Documentación:**
  - `README.md`, sección "Límites conocidos": quitar la entrada de la limitación de patches
    concurrentes (ya resuelta), agregar `PHASE_CLEAN_CONFIDENCE` donde se documenten las demás env
    vars de fase, y dejar anotado el límite residual (ver "Qué no cubre" más abajo).
  - `docs/limitacion-clean-patches-concurrentes.md`: actualizar `**Estado:**` a "Resuelto por spec
    13", sin borrar el análisis (queda como referencia histórica del hallazgo).
  - `docker/docker-compose.yml` y `docker/docker-compose.e2e.yml`: agregar `PHASE_CLEAN_CONFIDENCE`
    junto a `PHASE_CLEAN_GRACE_MINUTES`/`PHASE_CLEAN_GRACE_HOURS` en `patch-monitor-worker`.
  - `Construction.md` §7: marcar la fila 13 como en curso/implementada según corresponda al cerrar.
  - `CLAUDE.md`, sección "Estado actual": sumar el spec 13 a la lista de specs implementadas cuando
    se cierre.

**Fuera de alcance (para otro spec, si hace falta):**

- Cambios en `PatchStateWorkflow` o `PatchRegistryWorkflow` — este spec no toca entity workflows ni
  su forma de persistir estado, por lo tanto no dispara la obligación de `Workflow.Patched` que fija
  `CLAUDE.md`.
- Cambios en `MonitorApi`/DTOs o en `web/` — el cambio es interno a discovery/phase resolution; la
  API ya expone `Reason` como string libre, no hace falta tocar contratos.
- Usar Worker Versioning / Build IDs de Temporal como fuente de evidencia alternativa — no
  distinguen "el código se quitó" de "esta ejecución no pasó por esa rama"; queda fuera.
- Un umbral configurable manualmente por patch (`PHASE_CLEAN_MIN_EXECUTIONS` fijo) — se descarta en
  favor del cálculo adaptativo por `p`/`confianza` (ver Decisiones).
- Detectar un patch cuyo `if` nunca se ejecutó en toda la ventana de discovery — ninguna estrategia
  basada solo en Event History puede distinguirlo de "el código se quitó"; queda como límite
  residual documentado, con el override manual (spec 08) como salida.

## Modelo de datos

No se introducen records nuevos. Único cambio: `PhaseOptions` gana un campo.

```csharp
public sealed record PhaseOptions(TimeSpan CleanGrace, double CleanConfidence)
{
    public static readonly TimeSpan DefaultCleanGrace = TimeSpan.FromHours(24);
    public const double DefaultCleanConfidence = 0.95;

    public static PhaseOptions FromEnvironment();
}
```

## Plan de implementación

1. **`PhaseOptions.CleanConfidence` + tests.** Agregar el campo, el parseo de
   `PHASE_CLEAN_CONFIDENCE` y el default `0.95`. `test/PatchMonitor.Tests/Phase/PhaseOptionsTests.cs`:
   ausente → default; valor válido (`0.9`) respetado; valores fuera de rango (`0`, `1`, `-0.5`,
   `1.5`) → default; valor no numérico (`"abc"`) → default. `dotnet test` en verde.

2. **Atribución por patch en `PatchDiscoveryService`.** Reemplazar `floating`/`AttributeFloating`
   por la atribución directa descrita en Alcance, reutilizando `Put(..., overwrite: false)`. Tests
   en `test/PatchMonitor.Tests/Discovery/PatchDiscoveryServiceTests.cs`: dos patches (A, B) en el
   mismo `workflowType`, una ejecución con solo el marker de A ⇒ B la ve como `Absent`; historia no
   legible de esa ejecución ⇒ B la ve como `Unknown` y el set de B sale truncado; una ejecución sin
   marker de ningún patch conocido de un `workflowType` sin patches descubiertos todavía no crea
   ningún `PatchDiscoveryResult`; dos `workflowType` distintos no se contaminan entre sí; los tests
   existentes que cubrían el comportamiento de `floating` (patch único) siguen en verde sin cambios
   de aserción. `dotnet test` en verde.

3. **Capa 1 (no saltar fases) en `PhaseResolver`.** Agregar la condición de `PresentDeprecated` al
   caso 3. Tests en `test/PatchMonitor.Tests/Phase/PhaseResolverTests.cs`: patch con marker
   `Present` (nunca deprecado), ejecuciones cerradas, y `Absent` posterior al cutoff ⇒ resuelve
   Coexistence, no Clean. Revisar y ajustar los tests existentes de Clean para que incluyan una
   ejecución `PresentDeprecated` en su fixture. `dotnet test` en verde.

4. **Capa 2 (evidencia adaptativa) en `PhaseResolver`.** Implementar el cálculo de `p` y `N` y la
   condición de conteo mínimo. Tests con los puntos de referencia: `p = 1` (todas con marker) ⇒
   `N = 1`, mismo comportamiento que hoy; `p = 0.5` ⇒ `N = 5`; `p = 0.1` ⇒ `N = 29`; menos de `N`
   ejecuciones `Absent` posteriores al cutoff ⇒ no Clean, con el `Reason` describiendo cuántas
   faltan; `p = 0` o sin evidencia previa ⇒ no se infiere Clean por este caso. `dotnet test` en
   verde.

5. **Documentación.** Aplicar los cambios de README, el doc de la limitación,
   `docker-compose.yml`/`docker-compose.e2e.yml`, `Construction.md` y `CLAUDE.md` listados en
   Alcance.

6. **Verificación end-to-end manual.** Repetir el recorrido de `docs/limitacion-clean-patches-concurrentes.md`
   contra `ssy-yardflow`: `_1001_` con `patchmonitor-test-v1` ya quitado del código,
   `gate-cierre-terminal-v1` todavía activo en el mismo workflow, `PHASE_CLEAN_GRACE_MINUTES=1` para
   acelerar la espera. Confirmar que el dashboard/API muestra `patchmonitor-test-v1` en fase Clean
   con `PhaseSource.Inferred` (no `Override`), sin tocar `gate-cierre-terminal-v1`. Registrar la
   evidencia (timestamps, respuesta de la API) en este spec antes de marcar los criterios de
   aceptación.

### Evidencia de la verificación end-to-end (2026-09-24)

La evidencia original del hallazgo (ejecuciones de `_1001_` del 2026-09-22) ya había expirado de la
Visibility de Temporal por retención — `temporal workflow list` no devolvía ninguna ejecución de
`_1001_TruckScrapPurchaseWorkflow`. Se regeneró el ciclo completo contra el cluster local de
`ssy-yardflow` (`docker-compose.yml` + overlay de PatchMonitor con `PHASE_CLEAN_GRACE_MINUTES=1`),
agregando temporalmente el patch de prueba en `_1001_TruckScrapPurchaseWorkflow.ExecuteAsync` (sin
commitear, revertido al terminar — confirmado con `git status`/`git diff` limpio sobre ese archivo):

| Paso | Acción | Fase resultante | `source` |
| --- | --- | --- | --- |
| 1 | `Workflow.Patched("patchmonitor-test-v1")` condicional, ejecución `patchmonitor-test-phase1-run1` | Coexistence | Inferred (`"marker más reciente sin deprecar"`) |
| 2 | `Workflow.DeprecatePatch("patchmonitor-test-v1")` sin condicional, ejecución `patchmonitor-test-phase2-run1` | Deprecated | Inferred (`"marker más reciente deprecado"`) |
| 3 | Patch quitado del código, ejecución `patchmonitor-test-phase3-run1`, esperado el grace de 1 min | **Clean** | **Inferred** (`"sin marker desde 2026-09-24T17:41:54.74Z; código limpio"`) |

Respuesta de `GET /patches/default/_1001_TruckScrapPurchaseWorkflow/patchmonitor-test-v1` en el paso
3 (`revision: 6`, `lastChangedAt: 2026-09-24T17:44:37.57Z`):

```json
{"summary":{"phase":3,"source":0,"outcome":null,"nextPhase":null,"hasOverride":false},
 "phaseReason":"sin marker desde 2026-09-24T17:41:54.7445920+00:00; código limpio"}
```

`gate-cierre-terminal-v1` (el patch permanente concurrente en el mismo `workflowType`) se consultó en
paralelo y no se vio afectado por el cambio — siguió en su última fase observada, sin ningún
`Reason` ni `Revision` alterados por la inferencia de `patchmonitor-test-v1`. Las tres ejecuciones
sintéticas se cerraron (dos por fallo al no encontrar backends reales, una terminada manualmente) sin
dejar nada abierto.

## Criterios de aceptación

- [x] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [x] `dotnet test PatchMonitor.sln` pasa en verde, sin Docker. (292/292; el único fallo intermedio
      fue el flaky conocido de `TemporalPatchStateStoreTests` bajo carga paralela, documentado en
      `CLAUDE.md`, reproducido aislado sin cambios.)
- [x] Los tests nuevos de los pasos 1 a 4 existen y pasan, incluyendo los puntos de referencia de la
      Capa 2 (`p=1→N=1`, `p=0.5→N=5`, `p=0.1→N=29`).
- [x] Contra `ssy-yardflow`, con dos patches activos en el mismo `workflowType`
      (`gate-cierre-terminal-v1` activo, `patchmonitor-test-v1` ya limpio del código),
      `patchmonitor-test-v1` llega a fase Clean con `PhaseSource.Inferred`, sin usar el override
      manual. Ver "Evidencia de la verificación end-to-end" arriba.
- [x] `README.md` ya no lista la limitación de patches concurrentes entre los "Límites conocidos",
      documenta `PHASE_CLEAN_CONFIDENCE` y deja anotado el límite residual del `if` nunca ejecutado.
- [x] `docs/limitacion-clean-patches-concurrentes.md` tiene `**Estado:**` actualizado a resuelto por
      este spec.
- [x] `PHASE_CLEAN_CONFIDENCE` está declarada en `docker-compose.yml` y `docker-compose.e2e.yml`.

## Decisiones

- **Sí:** atribuir `Absent`/`Unknown` por `PatchKey` individual en vez de por bucket `floating`
  compartido de todo el `workflowType`. Es la causa raíz confirmada del bloqueo estructural.
- **Sí:** aceptar el efecto colateral en el gate 1→2 (`CoexistenceToDeprecatedGate`, spec 02): una
  ejecución abierta que solo trae el marker de otro patch del mismo `workflowType` ahora cuenta
  como "sin marker" para este patch y puede bloquear el salto 1→2 más tiempo que hoy. Nunca produce
  un `Ready` falso, solo bloquea de más — coherente con la regla existente de "ante la duda, no
  avanzar" (`IsTruncated` → `Inconclusive`).
- **Sí:** mitigar el falso Clean con dos capas (Deprecated obligatorio + evidencia adaptativa) en
  vez de solo documentar el riesgo. El costo de implementación es bajo (cálculo puro dentro de
  `PhaseResolver`) y el riesgo se vuelve más frecuente justo por este mismo spec.
- **Sí:** Capa 2 adaptativa (`p`/`confianza`) en vez de un umbral fijo (`PHASE_CLEAN_MIN_EXECUTIONS`
  constante). Un patch en el camino principal (`p≈1`) se comporta igual que hoy sin perder
  velocidad; un patch en una rama rara exige mucha más evidencia, sin que el operador tenga que
  adivinar un número por proyecto.
- **No:** usar Worker Versioning / Build IDs como evidencia de "código limpio". Requeriría que todos
  los proyectos observados adopten esa función de Temporal; el monitor es agnóstico al proyecto
  observado por diseño (`CLAUDE.md`).
- **No:** limitar el cambio de atribución por patch solo al cálculo de Clean, dejando los gates con
  el comportamiento viejo. Separar dos fuentes de evidencia (una para gates, otra para Clean)
  duplica lógica en `PatchDiscoveryService` sin necesidad; el efecto en el gate 1→2 es aceptable
  (ver arriba).
- **No:** resolver también el caso de un patch cuyo `if` nunca se ejecutó en la ventana. No es
  distinguible de "código quitado" con datos de Event History; queda como límite residual con el
  override manual como salida, igual que otros casos de `Inconclusive`.

## Riesgos identificados

| Riesgo | Mitigación |
| --- | --- |
| Con poco tráfico o una ventana de discovery corta, el `N` requerido por la Capa 2 puede no alcanzarse nunca dentro del `LookbackDays` configurado. | El patch queda en Deprecated indefinidamente (no en un estado roto); el override manual (spec 08) sigue disponible para cerrar la fase a mano. |
| Historias largas con `DISCOVERY_MAX_HISTORIES` bajo dejan más ejecuciones en `Unknown` tras este cambio (antes contribuían al bucket `floating` igual; ahora cuentan como `Unknown` por patch). | Ya cubierto por el mecanismo existente: `IsTruncated = true` fuerza `Inconclusive`, nunca un `Ready`/`Clean` falso. |
| Los tests existentes de `PhaseResolverTests` que infieren Clean sin una ejecución `PresentDeprecated` en su fixture dejan de pasar tras la Capa 1. | Se ajustan explícitamente en el paso 3 del plan de implementación; no es un riesgo de producción, es trabajo de migración de tests ya previsto. |

## Qué **no** está en este spec

- Cambios en `PatchStateWorkflow`, `PatchRegistryWorkflow`, `MonitorApi` o `web/`.
- Worker Versioning / Build IDs de Temporal como fuente de evidencia.
- Un umbral fijo configurable manualmente por patch (`PHASE_CLEAN_MIN_EXECUTIONS`).
- Detección de un patch cuyo código condicional nunca se ejerció en la ventana de discovery — límite
  residual, documentado en README y en `docs/limitacion-clean-patches-concurrentes.md`.

Cada uno de estos, si hace falta, va en su propio spec.
