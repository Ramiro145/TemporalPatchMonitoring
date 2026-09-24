# Limitación conocida: fase "Clean" no se infiere con patches concurrentes en el mismo workflow type

**Estado:** hallazgo confirmado en código, sin spec asociada todavía. Para referencia al planificar
una spec de arreglo (candidata a spec 13, siguiendo la numeración de `Construction.md`).

## Resumen

Cuando un `workflowType` tiene **más de un patch activo a la vez**, ninguno de esos patches puede
llegar a fase `Clean` por inferencia automática, aunque su código individual ya esté 100% limpio.

## Causa raíz

`PatchDiscoveryService` (spec 03, bucket "floating", `MarkerPresence.Absent`, líneas ~213-220 de
`specs/03-patch-discovery-two-tier.md`): una ejecución solo se atribuye como `Absent` (evidencia de
"código limpio") a un patch cuando esa ejecución **no tiene ningún marker `core_patch` en toda su
historia, para ningún patch** de ese mismo `workflowType`.

Esto es diseño intencional: la Tier 2 busca *cualquier* marker `core_patch` en la historia sin
filtrar por `patchId` primero, y solo si no encuentra ninguno la ejecución cuenta como "pre-patch"
(floating) para todos los patches candidatos de ese workflow type.

El problema aparece cuando conviven varios patches en el mismo workflow type: si **cualquiera** de
ellos sigue activo (todavía emite su marker), ninguna ejecución nueva va a estar nunca "limpia de
todo marker" → el bucket floating queda vacío → `Absent` nunca se calcula para ningún patch de ese
workflow type, ni siquiera para el que ya no tiene ningún rastro en el código.

## Cómo se reprodujo

Prueba local con un patch de prueba (`patchmonitor-test-v1`) sobre un workflow (`_1001_` de
ssy-yardflow) que hereda de una clase base con su propio patch permanente (`gate-cierre-terminal-v1`):

1. `Workflow.Patched(id)` → detectado como **Coexistence** (automático, correcto).
2. `Workflow.DeprecatePatch(id)` sin condicional → detectado como **Deprecated** (automático, correcto).
3. Se quitó toda mención al patch del código (`PHASE_CLEAN_GRACE_MINUTES=1` para acelerar la espera)
   → **nunca se infirió `Clean`**, ni con varios minutos de ejecuciones cerradas sin el marker del
   patch de prueba.

Motivo confirmado: como el workflow hereda de una clase base con un patch permanente propio, toda
ejecución siempre trae al menos ese marker → nunca cae en "floating" → `Absent` nunca se evalúa para
el patch de prueba, aunque ya estuviera limpio.

## Impacto

Cualquier workflow type con **más de un patch activo a la vez** (patrón común: un patch base/
permanente heredado + N patches propios del workflow, como en los Gate workflows de ssy-yardflow —
`_1007_` sola tiene 4 patches activos) no puede llegar a fase `Clean` por inferencia automática para
ninguno de sus patches individuales, mientras quede cualquier otro patch activo en ese mismo
workflow type.

No rompe el override manual (`POST /patches/.../override`, salto legal 2→3): se probó y funciona
bien, fuerza la fase y el dashboard la refleja (`Clean`, gate final). Pero cambia la expectativa
operativa en proyectos con patches concurrentes: cerrar un patch a fase 3 va a requerir casi siempre
override manual, no "esperar a que el gate se ponga verde solo".

Esto ya estaba anticipado como sin validar en el README (sección "Límites conocidos": *"Sin validar
todavía contra un cluster real: varios patches simultáneos..."*) — este documento confirma que, al
validarlo, el comportamiento no es solo "sin probar" sino un bloqueo estructural del diseño actual
de Tier 2.

## Posibles caminos de arreglo (sin decidir todavía)

- Cambiar `Absent` para que se evalúe **por patch** (ausencia del marker específico de ESE
  `patchId` en la historia) en vez de por bucket "floating" de todo el workflow type sin ningún
  marker. Requiere revisar si hay una razón de diseño detrás de la decisión actual (spec 03) antes
  de tocarla — podría existir para evitar falsos positivos cuando dos patches se solapan en el
  tiempo de introducción.
- Documentar la limitación explícitamente en el README junto a los demás "Límites conocidos", sin
  cambiar el comportamiento, dejando claro que fase 3 con patches concurrentes requiere override.

## Pendiente

- Decidir si esto amerita una spec nueva (13) o si alcanza con documentarlo en el README como
  limitación conocida.
- Si se resuelve con cambio de diseño, definir el criterio para "limpio por patch" sin introducir
  falsos positivos cuando dos patches nuevos se introdujeron cerca en el tiempo.
