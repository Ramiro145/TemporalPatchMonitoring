# Auditoría técnica — PatchMonitor

Oct 2, 2026 · @Jose

El código compila limpio y 291 de 292 tests pasan, pero **tres defectos de severidad alta comprometen lo que el sistema promete**: un fallo del webhook se reporta como notificación enviada y no se reintenta nunca, las ejecuciones `ContinuedAsNew` producen bloqueos falsos, y los workflows leen configuración del entorno dentro del código de workflow — lo que los expone al mismo `NonDeterminismError` que el monitor existe para prevenir.

## Alcance y método

Se revisó el repositorio completo en su commit `dc208ec` ("spec 13 implementada"), rama `main`: 4 proyectos .NET 8 (`Contracts`, `Common`, `PatchMonitor`, `MonitorApi`), el dashboard React en `web/`, la suite de 292 tests y el stack de `docker/`.

La revisión fue por lectura de código contrastada contra las specs y el `Construction.md` del propio repositorio, más dos verificaciones ejecutadas:

| Verificación | Comando | Resultado |
| --- | --- | --- |
| Compilación | `dotnet build PatchMonitor.sln` | Éxito, 0 advertencias, 0 errores |
| Suite completa | `dotnet test PatchMonitor.sln` | 291 pasan, **1 falla** (`PatchStateWorkflowTests.Primer_assessment_deja_revision_en_uno`) |
| Ese test aislado | `dotnet test --filter …` | Pasa — el fallo es de concurrencia entre tests, ver hallazgo A-4 |

Quedó fuera del alcance: validación contra un cluster de Temporal real, pruebas de carga, revisión del dashboard más allá de su capa de datos y dependencias, y auditoría de las specs como documento (se usaron como referencia de intención, no como objeto de revisión).

## Resumen de hallazgos

Veintiun hallazgos: 4 de severidad alta, 9 media y 8 baja. Los cuatro altos atacan funciones centrales — notificar, decidir si un gate está bloqueado, y la determinación de los propios workflows del monitor.

| ID | Severidad | Área | Hallazgo | Ubicación |
| --- | --- | --- | --- | --- |
| A-1 | Alta | Notificación | Un fallo del webhook se reporta como enviado y no se reintenta nunca | `CompositeNotifier.cs:40` |
| A-2 | Alta | Dominio | `ContinuedAsNew` se cuenta como ejecución abierta → `Blocked` falsos | `ExecutionStatus.cs:28` |
| A-3 | Alta | Workflows | Configuración leída desde el entorno dentro del código de workflow | `MonitorWorkflow.cs:32` |
| A-4 | Alta | Pruebas | La suite falla al correr completa por colisión de variables de entorno | `PatchStateWorkflowTests.cs` |
| M-1 | Media (opcional - posible uso con token) | Seguridad | La API de control no tiene autenticación | `MonitorApi/Program.cs` |
| M-2 | Media | Recursos | `/health/workflow` abre un cliente de Temporal por request y no lo cierra | `WorkflowStarter.cs:20` |
| M-3 | Media | Operación | El Schedule se crea pero nunca se actualiza; el remedio documentado no aplica | `ScheduleBootstrapper.cs:55` |
| M-4 | Media | Operación | El worker muere si el cluster no está arriba y no vuelve | `docker-compose.yml` |
| M-5 | Media | Resiliencia | El `Lazy` cachea la conexión fallida: no hay reconexión sin reinicio | `ServiceCollectionExtensions.cs:53` |
| M-6 | Media | Resolución de fase | Un patch en `Clean` regresa a `Unknown` y notifica un retroceso falso | `PhaseResolver.cs:120` |
| M-7 | Media | Descubrimiento | Los topes por defecto se contradicen: todo queda `Inconclusive` | `DiscoveryOptions.cs:26` |
| M-8 | Media | API | `GET /runs` puede no mostrar las corridas más recientes | `MonitorRunReader.cs:34` |
| M-9 | Media | Workflows | `MaxPatchesPerRun` sin orden estable: hay patches que nunca se evalúan | `MonitorWorkflow.cs:50` |
| B-1 | Baja | API | `Unreadable` acepta un motivo y lo descarta | `PatchResponses.cs:37` |
| B-2 | Baja | Dominio | El saneado de `PatchKey` puede colisionar entre dos patches distintos | `PatchKey.cs:46` |
| B-3 | Baja | Descubrimiento | Tier 1 asume `Present` con historia ilegible y puede retroceder la fase | `PatchDiscoveryService.cs:85` |
| B-4 | Baja | Dominio | Rama muerta de `Clean` en `PhaseEvaluator`, contradice el contrato de la API | `PhaseEvaluator.cs:39` |
| B-5 | Baja | API | `GET /patches` hace N+1 secuencial, hasta 100 queries por request | `PatchEndpoints.cs:24` |
| B-6 | Baja | Frontend | La CLI `shadcn` está en `dependencies`; `cn` no resuelve conflictos de Tailwind | `web/package.json` |
| B-7 | Baja | Seguridad | Los contenedores corren como root | `docker/Dockerfile.*` |
| B-8 | Baja | Plumbing | "No encontrado" se detecta comparando el texto del error | `WorkflowValidator.cs:27` |

## Hallazgos de severidad alta

### A-1 — Un fallo del webhook se reporta como enviado y no se reintenta nunca

`NotificationActivities.NotifyVerdictChangeAsync` reclama la revisión en el entity *antes* de invocar al notificador, y `CompositeNotifier` solo lanza si **todos** los notificadores fallan (`CompositeNotifier.cs:40`). Como `StructuredLogNotifier` escribe a `Console` y nunca falla, un webhook caído queda tragado: la activity devuelve `true`, el `MonitorWorkflow` cuenta `notificationsSent++`, y el claim ya quedó tomado, así que ninguna pasada posterior vuelve a intentarlo.

Consecuencia de segundo orden: `NOTIFIER_MAX_ATTEMPTS` y toda la clasificación reintentable / no reintentable de `WebhookNotifier` (`408`, `429`, `5xx` frente a `4xx`) es código inalcanzable — la `RetryPolicy` de la activity solo podría dispararse en un escenario que no puede ocurrir. Quien dependa del webhook pierde avisos en silencio y el dashboard le dirá que se enviaron.

**Corrección.** Invertir el orden: enviar primero, reclamar después del envío exitoso; o bien hacer que el claim sea reversible ante un fallo. Y que `CompositeNotifier` propague el fallo de cualquier destino configurado explícitamente, en vez de exigir que fallen todos — el log local no debería contar como destino que absuelve.

### A-2 — `ContinuedAsNew` se cuenta como ejecución abierta

`ExecutionStatus.IsOpen()` incluye `ContinuedAsNew` (`ExecutionStatus.cs:28`), siguiendo lo que el spec 02 declara. En Temporal una run con estado `CONTINUED_AS_NEW` está **cerrada**: su sucesora aparece por separado como `Running` y ya se evalúa por su cuenta. Contarla como abierta no agrega información y bloquea el gate durante toda la ventana de `DISCOVERY_LOOKBACK_DAYS`.

Además contradice la restricción #3 de `Construction.md:88`, que apoya todo el diseño del spec 05 en que el `Continue-As-New` es justamente lo que saca a los entity workflows del gate 1→2. Con esta definición no los saca: cada run previa sigue contando como abierta. El caso no tiene **ningún test** y el spec lo afirma sin justificarlo.

**Corrección.** Sacar `ContinuedAsNew` de `IsOpen()` y actualizar el spec 02 con la razón. Agregar un test del gate 1→2 con una run `ContinuedAsNew` sin marker más su sucesora `Running` con marker: debe dar `Ready`.

### A-3 — Configuración leída desde el entorno dentro del código de workflow

`MonitorWorkflow.RunAsync` llama `MonitorOptions.FromEnvironment()` y `NotificationOptions.FromEnvironment()` en sus primeras líneas (`MonitorWorkflow.cs:32`); `PatchStateWorkflow` hace lo mismo con `StateOptions` en su `[WorkflowInit]`. Leer el entorno es una operación no determinística: si cambia entre la grabación y un replay — worker reiniciado, despliegue progresivo con otro `MONITOR_MAX_PATCHES_PER_RUN` o `PATCH_STATE_CAN_THRESHOLD` — la secuencia de comandos cambia y la ejecución muere con `NonDeterminismError`.

El SDK de .NET no corre los workflows en un sandbox que lo impida, así que no hay nada que avise. En un sistema cuyo propósito es prevenir exactamente ese error, el detalle importa más que en otro.

**Corrección.** Resolver las opciones fuera del workflow: pasarlas como argumento desde el Schedule, o leerlas en una activity cuyo resultado queda grabado en la historia. El comentario de `PatchStateWorkflow.cs:45` presenta la relectura tras el `Continue-As-New` como una ventaja ("un cambio de umbral aplica sin redeploy"); es un intercambio consciente, pero el precio es determinismo y conviene decidirlo explícitamente.

### A-4 — La suite falla al correr completa

`dotnet test PatchMonitor.sln` deja `PatchStateWorkflowTests.Primer_assessment_deja_revision_en_uno` en rojo; el mismo test aislado pasa. Causa: `ContinueAsNewTests` y `StateOptionsTests` mutan variables de entorno del proceso y por eso comparten la colección `state-env-vars`, pero `PatchStateWorkflowTests` **no está en esa colección** y su workflow lee el entorno al arrancar. Con un `PATCH_STATE_CAN_THRESHOLD` bajo puesto por otro test en paralelo, el entity hace `Continue-As-New` apenas recibe el primer assessment y `AssessmentCount` vuelve a 0, rompiendo la aserción.

Es el síntoma visible de A-3, y además deja la suite sin valor como compuerta: un rojo intermitente se normaliza rápido.

**Corrección.** Inmediata: agregar `[Collection(EnvVarCollection.Name)]` a toda clase que arranque un `PatchStateWorkflow`. De fondo: resolver A-3 y el acoplamiento desaparece.

## Hallazgos de severidad media

### M-1 — La API de control no tiene autenticación

`POST /patches/{ns}/{type}/{patchId}/override`, `/schedule/pause`, `/schedule/unpause` y `/schedule/trigger` están abiertos en `0.0.0.0:5100`, sin autenticación ni autorización, con Swagger habilitado siempre (el comentario de `Program.cs:50` lo declara deliberado). El override fuerza la fase de un patch, que es el insumo de una decisión de despliegue: quien lo mueva puede hacer que el equipo borre código de un patch con ejecuciones vivas. No figura en "Límites conocidos" del README.

### M-2 — `/health/workflow` abre una conexión por request y no la cierra

`WorkflowStarter.StartAsync` hace su propio `TemporalClient.ConnectAsync` en cada llamada (`WorkflowStarter.cs:20`) y nunca libera el cliente. Combinado con M-1, cualquiera puede agotar las conexiones del proceso con un bucle de requests. El endpoint además arranca workflows sin tope.

### M-3 — El Schedule se crea pero nunca se actualiza

`EnsureScheduleAsync` traga `ScheduleAlreadyRunningException` y devuelve `false` (`ScheduleBootstrapper.cs:58`): cambiar `MONITOR_INTERVAL_MINUTES` o `MONITOR_CATCHUP_WINDOW_MINUTES` no tiene ningún efecto sobre un Schedule ya creado. El README indica `docker compose down -v` como remedio, pero en el caso de uso principal — un cluster de Temporal externo, que es lo que el compose base configura — **no hay volumen del monitor que borrar**: el Schedule vive en el cluster ajeno y sobrevive a cualquier `down -v`. El remedio documentado no funciona.

**Corrección.** Comparar el spec vigente contra el deseado y hacer `UpdateAsync` cuando difieran; o documentar el borrado explícito del Schedule.

### M-4 — El worker muere si el cluster no está arriba y no vuelve

Ningún servicio del `docker-compose.yml` declara `restart:`. Los propios comentarios del archivo se contradicen: las líneas 27-31 explican que el bridge nativo mata el proceso con exit 139 y que "`WorkerHost.ConnectAsync` no reintenta", mientras las líneas 110-112 afirman que la conexión y el `NamespaceBootstrapper` "reintentan solos". La segunda afirmación es falsa, y es la que gobierna el perfil por defecto.

### M-5 — El `Lazy` cachea la conexión fallida

`Lazy<Task<ITemporalClient>>` guarda la tarea, no el resultado: si el primer `ConnectAsync` falla, la tarea fallida queda cacheada y toda llamada posterior recibe la misma excepción. El proceso no reconecta nunca sin reinicio. Afecta a `TemporalExecutionSource` y al registro de DI de `ServiceCollectionExtensions.cs:53`, y agrava M-4.

### M-6 — Un patch en `Clean` regresa a `Unknown`

Cuando las ejecuciones con marker salen de la ventana de lookback, `withMarker.Count == 0` y la resolución cae al caso 6: `Unknown` (`PhaseResolver.cs:120`). Para un patch que ya había llegado a fase 3 — el final feliz — eso cambia el estado, incrementa `Revision` y dispara una notificación de retroceso que no refleja ningún hecho nuevo. Con `DISCOVERY_LOOKBACK_DAYS=7` ocurre a la semana de cada patch terminado.

### M-7 — Los topes por defecto se contradicen

`DISCOVERY_MAX_EXECUTIONS=500` pero `DISCOVERY_MAX_HISTORIES=200`. Pasadas las 200 ejecuciones, el resto queda en `MarkerPresence.Unknown`, `IsTruncated` se pone en `true` y **todos** los patches salen `Inconclusive` de forma permanente. Con la configuración de fábrica, en cualquier namespace de tamaño medio el monitor deja de responder la pregunta para la que existe. El README advierte sobre dimensionar los topes, pero no sobre que los defaults ya son incoherentes entre sí.

### M-8 — `GET /runs` puede no mostrar las corridas más recientes

`TemporalMonitorRunReader` escanea hasta 500 ejecuciones sin `ORDER BY` — la standard visibility no lo admite, y el comentario del archivo lo reconoce — y ordena en memoria. Con el tick de 5 minutos son unas 288 corridas por día y 7 días de retención: el corte de 500 no garantiza que las últimas estén entre las escaneadas, así que el panel de "últimas corridas" puede mostrar corridas viejas.

### M-9 — `MaxPatchesPerRun` sin orden estable

`discovered.Take(options.MaxPatchesPerRun)` (`MonitorWorkflow.cs:50`) recorta una lista cuyo orden proviene de la enumeración de un `Dictionary` en `PatchDiscoveryService`. Con más de 50 patches, cuáles se evalúan queda librado a ese orden: algunos pueden no evaluarse nunca, sin que nada lo reporte. Un orden determinístico, o un recorrido rotativo entre corridas, lo resuelve.

## Hallazgos de severidad baja

Ninguno compromete una decisión; todos son deuda barata de saldar.

| ID | Hallazgo | Detalle | Ubicación |
| --- | --- | --- | --- |
| B-1 | `Unreadable` descarta el motivo | El método recibe `reason` y no lo pone en la respuesta: el operador nunca ve por qué un patch no se pudo leer | `PatchResponses.cs:37` |
| B-2 | Colisión de saneado en `PatchKey` | `Sanitize` mapea todo carácter inseguro a `_`, así que `"a b"` y `"a_b"` resuelven al mismo entity workflow y comparten estado | `PatchKey.cs:46` |
| B-3 | Tier 1 asume `Present` con historia ilegible | Si la Event History no se pudo leer pero el atributo declara el patch, se asigna `Present` (fase 1): puede hacer retroceder la fase inferida de `Deprecated` a `Coexistence` | `PatchDiscoveryService.cs:85` |
| B-4 | Rama muerta de `Clean` en `PhaseEvaluator` | Devuelve `Blocked` para la fase final, pero `AssessPatch` nunca la invoca con `Clean`; además contradice el `outcome: null` que documenta el README | `PhaseEvaluator.cs:39` |
| B-5 | `GET /patches` hace N+1 secuencial | Hasta 100 queries a Temporal por request, una tras otra; el front lo mitiga bajando el refetch, pero no está paralelizado | `PatchEndpoints.cs:24` |
| B-6 | Dependencias del dashboard | La CLI `shadcn` está en `dependencies` en vez de `devDependencies`; `cn` reemplaza al par `clsx` + `tailwind-merge` y no resuelve conflictos de clases Tailwind | `web/package.json` |
| B-7 | Contenedores como root | Ningún Dockerfile declara `USER`; los tres servicios corren con privilegios innecesarios | `docker/Dockerfile.*` |
| B-8 | "No encontrado" por comparación de texto | `WorkflowValidator` detecta el caso buscando la cadena `"no rows in result set"`; se rompe ante cualquier cambio de mensaje del servidor | `WorkflowValidator.cs:27` |

## Fortalezas

Vale decirlo porque condiciona cómo encarar las correcciones: la base es buena y los hallazgos altos son puntuales, no estructurales.

- **La separación de capas se sostiene.** `Contracts` no referencia el SDK de Temporal en ninguna parte; el único archivo del descubrimiento que toca `Temporalio` es `TemporalExecutionSource`. Eso es lo que hace que los gates y el resolver sean verificables sin cluster, y por eso la suite tiene 292 tests sin Docker.
- **El tri-estado `Inconclusive` es la decisión correcta** y es poco común: distinguir "no sé" de "todavía no" evita el `Ready` falso, que en este dominio es el único error que no se puede permitir. La distinción paralela entre `Absent` y `Unknown` en `MarkerPresence` responde al mismo criterio.
- **La atribución de ausencia por patch del spec 13** resuelve un problema real y bien diagnosticado — la reproducción documentada en `docs/limitacion-clean-patches-concurrentes.md` es un buen trabajo de análisis de causa raíz, y la mitigación adaptativa del falso `Clean` está bien pensada.
- **La asimetría de los gates vive en archivos separados**, no en un parámetro booleano. Es la decisión que evita que alguien "unifique" dos predicados que no son el mismo.
- **La documentación de las decisiones es mejor que la media.** Las restricciones del servidor de visibility, el porqué de las llamadas gRPC crudas y las trampas conocidas están escritas donde hacen falta. Varios de los hallazgos de este informe se apoyan en esos propios comentarios.
