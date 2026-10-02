using Contracts.Domain;
using PatchMonitor.Activities;
using PatchMonitor.Tests.Monitor;
using Temporalio.Exceptions;
using Xunit;

namespace PatchMonitor.Tests.Activities;

/// <summary>
/// Spec 17: una query que falla por replay roto no se reintenta. La Activity la convierte en un
/// <see cref="ApplicationFailureException"/> no reintentable; cualquier otro error pasa intacto
/// para que la <c>RetryPolicy</c> siga reintentando los fallos transitorios.
/// </summary>
public class QueryFailureGuardTests
{
    private static readonly PatchKey Key = new("default", "OrderWorkflow", "order-v2");

    private static WorkflowQueryFailedException QueryFailure() =>
        new("[TMPRL1100] Nondeterminism error: simulado");

    [Fact]
    public async Task Un_query_fallido_se_convierte_en_fallo_no_reintentable()
    {
        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            QueryFailureGuard.RunAsync<int>(() => throw QueryFailure()));

        Assert.True(ex.NonRetryable);
        Assert.Contains("Nondeterminism", ex.Message);
        Assert.IsType<WorkflowQueryFailedException>(ex.InnerException);
    }

    [Fact]
    public async Task Otros_errores_pasan_intactos_para_que_se_reintenten()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            QueryFailureGuard.RunAsync<int>(() => throw new InvalidOperationException("transitorio")));
    }

    [Fact]
    public async Task GetPatchStateAsync_con_replay_roto_falla_no_reintentable()
    {
        var store = new FakePatchStateStore();
        store.FailGetWith(Key, QueryFailure());
        var activities = new PatchStateActivities(store, new PatchMonitor.Services.InMemoryPhaseOverrideStore());

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(() => activities.GetPatchStateAsync(Key));

        Assert.True(ex.NonRetryable);
    }

    [Fact]
    public async Task Un_resultado_normal_no_se_toca()
    {
        var result = await QueryFailureGuard.RunAsync(() => Task.FromResult(42));

        Assert.Equal(42, result);
    }
}
