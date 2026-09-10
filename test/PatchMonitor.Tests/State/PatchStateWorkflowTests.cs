using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using Contracts.Workflows;
using PatchMonitor.Workflows;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace PatchMonitor.Tests.State;

/// <summary>
/// Acumulación y detección de cambio del entity de estado, sobre
/// <see cref="WorkflowEnvironment.StartTimeSkippingAsync"/>. El <c>Continue-As-New</c> y los
/// updates de override se prueban en sus propios archivos.
/// </summary>
public class PatchStateWorkflowTests
{
    private static readonly PatchKey Key = new("default", "OrderWorkflow", "order-v2");
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static PhaseResolution Resolution(PatchPhase phase, DateTimeOffset at) =>
        new(phase, PhaseSource.Inferred, $"Inferida {phase}", at);

    private static PhaseVerdict Verdict(GateOutcome outcome, PatchPhase current, PatchPhase? next, DateTimeOffset at) =>
        new(outcome, current, next, 0, Array.Empty<string>(), $"{outcome}", at);

    private static PatchAssessmentInput Assessment(
        PatchPhase phase, PhaseVerdict? verdict, DateTimeOffset at) =>
        new(Key, Resolution(phase, at), verdict, at);

    private static async Task RunAsync(Func<WorkflowHandle<IPatchStateWorkflow>, Task> body)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"state-{Guid.NewGuid():N}";
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchStateWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                (IPatchStateWorkflow wf) => wf.RunAsync(Key, null),
                new WorkflowOptions(id: $"state-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));

            await body(handle);
        });
    }

    [Fact]
    public async Task Primer_assessment_deja_revision_en_uno()
    {
        await RunAsync(async handle =>
        {
            var verdict = Verdict(GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated, T0);
            await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                Assessment(PatchPhase.Coexistence, verdict, T0)));

            var state = await handle.QueryAsync(wf => wf.GetState());

            Assert.Equal(1, state.Revision);
            Assert.Equal(1, state.AssessmentCount);
            Assert.Null(state.PreviousVerdict);
            Assert.Equal(PatchPhase.Coexistence, state.Phase);
            Assert.Equal(GateOutcome.Blocked, state.LastVerdict?.Outcome);
            Assert.Single(state.History);
            Assert.Equal(T0, state.LastChangedAt);
        });
    }

    [Fact]
    public async Task Segundo_assessment_identico_no_mueve_revision_pero_si_el_contador()
    {
        await RunAsync(async handle =>
        {
            var verdict = Verdict(GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated, T0);
            await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                Assessment(PatchPhase.Coexistence, verdict, T0)));
            await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                Assessment(PatchPhase.Coexistence, verdict, T0.AddMinutes(5))));

            var state = await handle.QueryAsync(wf => wf.GetState());

            Assert.Equal(1, state.Revision);
            Assert.Equal(2, state.AssessmentCount);
            Assert.Single(state.History);
            Assert.Equal(T0.AddMinutes(5), state.LastObservedAt);
            Assert.Equal(T0, state.LastChangedAt);
        });
    }

    [Fact]
    public async Task Tercer_assessment_con_otra_fase_avanza_revision_y_guarda_el_veredicto_anterior()
    {
        await RunAsync(async handle =>
        {
            var first = Verdict(GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated, T0);
            var third = Verdict(GateOutcome.Ready, PatchPhase.Deprecated, PatchPhase.Clean, T0.AddMinutes(10));

            await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                Assessment(PatchPhase.Coexistence, first, T0)));
            await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                Assessment(PatchPhase.Coexistence, first, T0.AddMinutes(5))));
            await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                Assessment(PatchPhase.Deprecated, third, T0.AddMinutes(10))));

            var state = await handle.QueryAsync(wf => wf.GetState());

            Assert.Equal(2, state.Revision);
            Assert.Equal(3, state.AssessmentCount);
            Assert.Equal(PatchPhase.Deprecated, state.Phase);
            Assert.Equal(GateOutcome.Ready, state.LastVerdict?.Outcome);
            Assert.Equal(GateOutcome.Blocked, state.PreviousVerdict?.Outcome);
            Assert.Equal(2, state.History.Count);
            Assert.Equal(T0.AddMinutes(10), state.LastChangedAt);
        });
    }

    [Fact]
    public async Task SetOverride_a_deprecated_fuerza_la_fase_y_la_fuente()
    {
        await RunAsync(async handle =>
        {
            await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                Assessment(PatchPhase.Coexistence,
                    Verdict(GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated, T0), T0)));

            var ov = new PhaseOverride(Key, PatchPhase.Deprecated, "operador", T0.AddMinutes(1), null);
            await handle.ExecuteUpdateAsync(wf => wf.SetOverrideAsync(ov));

            var state = await handle.QueryAsync(wf => wf.GetState());

            Assert.Equal(PatchPhase.Deprecated, state.Phase);
            Assert.Equal(PhaseSource.Override, state.Source);
            Assert.Equal(ov, state.Override);
        });
    }

    [Fact]
    public async Task SetOverride_con_fase_unknown_es_rechazado_y_el_estado_no_cambia()
    {
        await RunAsync(async handle =>
        {
            await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                Assessment(PatchPhase.Coexistence,
                    Verdict(GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated, T0), T0)));

            var invalid = new PhaseOverride(Key, PatchPhase.Unknown, "operador", T0.AddMinutes(1), null);

            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(
                () => handle.ExecuteUpdateAsync(wf => wf.SetOverrideAsync(invalid)));

            var state = await handle.QueryAsync(wf => wf.GetState());

            Assert.Equal(PatchPhase.Coexistence, state.Phase);
            Assert.Equal(PhaseSource.Inferred, state.Source);
            Assert.Null(state.Override);
        });
    }

    [Fact]
    public async Task ClearOverride_devuelve_el_estado_a_la_fase_inferida_en_el_siguiente_assessment()
    {
        await RunAsync(async handle =>
        {
            var ov = new PhaseOverride(Key, PatchPhase.Deprecated, "operador", T0, null);
            await handle.ExecuteUpdateAsync(wf => wf.SetOverrideAsync(ov));
            await handle.ExecuteUpdateAsync(wf => wf.ClearOverrideAsync());

            await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                Assessment(PatchPhase.Coexistence,
                    Verdict(GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated, T0.AddMinutes(5)),
                    T0.AddMinutes(5))));

            var state = await handle.QueryAsync(wf => wf.GetState());

            Assert.Equal(PatchPhase.Coexistence, state.Phase);
            Assert.Equal(PhaseSource.Inferred, state.Source);
            Assert.Null(state.Override);
        });
    }
}
