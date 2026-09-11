# Fases del patch `audit-before-decision` en `ReleaseOrderDemo`

Guía operativa del spec 09 de `proyecto_monitoreo`: los tres diffs exactos que hay que aplicar,
uno por uno, sobre `src/ReleaseOrder/Workflows/ReleaseOrderWorkFlow.cs` del repo
**`ReleaseOrderDemo`** real (checkout local: `releaseorder-signal/releaseorder_combined`, **no**
este repo). Tomados literalmente de los specs `04-patching-versionado-drenaje.md`,
`05-deprecacion-patch-audit-before-decision.md` y `06-limpieza-patch-audit-before-decision.md` de
ese repo. Aplicar, rebuildear y redesplegar `release-orden-worker` es una acción manual del
operador — este documento no la ejecuta.

El código real de `ReleaseOrderDemo` ya recorrió las tres fases (specs 04/05/06 están en estado
`Implementado`) y hoy está en la **fase 3** (limpio). Para este spec 09 se **reintroduce** el
patch desde cero: se despliega la fase 1, luego la 2, luego la 3, repitiendo el ciclo para generar
la evidencia contra un cluster real.

Ubicación del bloque en todas las fases: inmediatamente antes de
`_status = "Waiting for release decision";` / `await Workflow.WaitConditionAsync(...)` en
`RunAsync(int orderId)`.

> **Nota operativa (ejecución real del 2026-09-11):** el bloque de la fase 1 mostrado abajo es el
> texto literal de la spec 04, pero **no** es el que se terminó aplicando en el worker real. El
> código "limpio" que ya corría en `ReleaseOrderDemo` (fase 3 de su propio ciclo) llama a
> `RecordAwaitingDecisionAsync` **incondicionalmente** — no hay ningún código "viejo sin la
> llamada" al que el `else` original pudiera volver. Aplicar el `else` literal (que no hace nada)
> sobre ejecuciones ya abiertas con esa historia produjo un `NonDeterminismError` real al
> drenarlas (`[TMPRL1100] No command scheduled for event ... ActivityTaskScheduled`): dos órdenes
> de prueba (`release-order-8009`, `release-order-8010`) quedaron irrecuperables y se terminaron
> (`temporal workflow terminate`). El bloque efectivamente desplegado agregó la misma llamada
> también en el `else`, para que la secuencia de comandos sea idéntica a la que el código limpio ya
> emitía — la única diferencia observable entre las ramas queda en el marker, que es todo lo que el
> monitor necesita para distinguir pre-patch de post-patch:
>
> ```csharp
> if (Workflow.Patched("audit-before-decision"))
> {
>     await Workflow.ExecuteActivityAsync(
>         (AuditActivities a) => a.RecordAwaitingDecisionAsync(orderId),
>         DefaultOptions);
> }
> else
> {
>     await Workflow.ExecuteActivityAsync(
>         (AuditActivities a) => a.RecordAwaitingDecisionAsync(orderId),
>         DefaultOptions);
> }
> ```
>
> Esto es específico de reintroducir el patch sobre un repo que ya completó su propio ciclo de vida
> real una vez; no es una corrección al texto de la spec 04 original (que sigue siendo correcto
> para un primer despliegue genuino del patch, sobre código que de verdad no llamaba la Activity).

## Fase 1 — `Workflow.Patched` (spec 04 de `ReleaseOrderDemo`)

```csharp
// El paso de auditoría (log-only) se agrega vía un patch versionado con Workflow.Patched.
// Ciclo de vida del patch (didáctico):
//   Fase 1 (acá): if/else — ejecuciones nuevas toman la rama nueva, las viejas en vuelo
//     siguen la rama vieja sin NonDeterminismError.
//   Fase 2: Workflow.DeprecatePatch(...) + paso incondicional, cuando no queda ninguna
//     ejecución vieja abierta.
//   Fase 3: código limpio, sin Patched ni DeprecatePatch, cuando ninguna historia con el
//     marker se va a reproducir.
if (Workflow.Patched("audit-before-decision"))
{
    await Workflow.ExecuteActivityAsync(
        (AuditActivities a) => a.RecordAwaitingDecisionAsync(orderId),
        DefaultOptions);
}
// else: código viejo — no hacía nada acá.
```

**Precondición para entrar:** ninguna, es el primer deploy del patch. Después de este deploy,
las ejecuciones nuevas escriben el marker `core_patch` (`patchId: "audit-before-decision"`, sin
`deprecated`) y las ejecuciones pre-patch en vuelo siguen la rama `else` sin
`WorkflowTaskFailed`/`NonDeterminismError`.

## Fase 2 — `Workflow.DeprecatePatch` (spec 05 de `ReleaseOrderDemo`)

```csharp
Workflow.DeprecatePatch("audit-before-decision");
await Workflow.ExecuteActivityAsync(
    (AuditActivities a) => a.RecordAwaitingDecisionAsync(orderId),
    DefaultOptions);
```

Reemplaza el `if (Workflow.Patched(...)) { ... }` completo (sin condicional ni `else`).

**Precondición para entrar:** no debe quedar ninguna ejecución `Running` de
`ReleaseOrderWorkflow` sin el marker `core_patch` (es decir, ninguna ejecución pre-patch abierta).
Verificar con:

```powershell
temporal workflow list --namespace default --query "ExecutionStatus='Running'" -o json
```

(o el filtro equivalente en la Temporal UI). Si aparece alguna ejecución pre-patch, no avanzar
hasta que termine o se decida explícitamente terminarla. Después de este deploy, las ejecuciones
nuevas siguen escribiendo el marker `core_patch`, ahora con el flag `deprecated` activo.

## Fase 3 — código limpio (spec 06 de `ReleaseOrderDemo`)

```csharp
// El paso de auditoría (log-only) se incorporó vía el patch "audit-before-decision", que
// ya recorrió su ciclo de vida completo (Patched -> DeprecatePatch -> este paso limpio).
// Ver README "Prueba H" y specs/04, /05 y /06 para el recorrido.
await Workflow.ExecuteActivityAsync(
    (AuditActivities a) => a.RecordAwaitingDecisionAsync(orderId),
    DefaultOptions);
```

Borra la línea `Workflow.DeprecatePatch("audit-before-decision");` y el bloque de comentario de
las tres fases, dejando solo el comentario corto de 3 líneas sobre el `ExecuteActivityAsync`
incondicional. No debe quedar ningún `Workflow.Patched` ni `Workflow.DeprecatePatch` en el
proyecto.

**Precondición para entrar:** no debe quedar ninguna ejecución de `ReleaseOrderWorkflow` (abierta
o cerrada) cuya historia tenga el marker `core_patch` y se pueda reproducir o consultar — una
barra más alta que la de la fase 2 (cero `Running` no alcanza; tiene que ser cero en cualquier
estado). Verificar con:

```powershell
temporal workflow list --namespace default --limit 100 -o json
temporal workflow list --namespace default --query "WorkflowType='ReleaseOrderWorkflow'" -o json
```

Después de este deploy, las ejecuciones nuevas dejan de escribir `MarkerRecorded core_patch` y
`UpsertWorkflowSearchAttributes TemporalChangeVersion`; el paso de auditoría sigue corriendo como
un paso más, sin ninguna mención al patch en el código.

## Comando de rebuild + redeploy (idéntico entre las tres fases)

Después de editar `ReleaseOrderWorkFlow.cs` con el diff de la fase correspondiente, desde la raíz
de `ReleaseOrderDemo`:

```powershell
docker compose -f docker/docker-compose.yml build release-orden-worker
docker compose -f docker/docker-compose.yml up -d --force-recreate release-orden-worker
```
