using Common.Temporal;
using Xunit;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// <see cref="ResettableAsyncLazy{T}"/> (spec 15, M-5): cachea éxitos, no cachea fallos.
/// </summary>
public class ResettableAsyncLazyTests
{
    [Fact]
    public async Task Un_exito_se_cachea_y_la_fabrica_corre_una_sola_vez()
    {
        var calls = 0;
        var lazy = new ResettableAsyncLazy<int>(() => Task.FromResult(++calls));

        var first = await lazy.GetValueAsync();
        var second = await lazy.GetValueAsync();

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Tras_un_fallo_la_llamada_siguiente_reintenta_y_puede_tener_exito()
    {
        var calls = 0;
        var lazy = new ResettableAsyncLazy<int>(() =>
            ++calls == 1
                ? Task.FromException<int>(new InvalidOperationException("cluster caído"))
                : Task.FromResult(42));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());
        Assert.Equal("cluster caído", ex.Message);

        Assert.Equal(42, await lazy.GetValueAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Una_fabrica_que_lanza_de_forma_sincrona_tambien_se_reintenta()
    {
        var calls = 0;
        var lazy = new ResettableAsyncLazy<int>(() =>
        {
            if (++calls == 1)
            {
                throw new InvalidOperationException("síncrona");
            }

            return Task.FromResult(7);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());

        Assert.Equal(7, await lazy.GetValueAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Los_fallos_repetidos_no_se_cachean()
    {
        var calls = 0;
        var lazy = new ResettableAsyncLazy<int>(() =>
        {
            calls++;
            return Task.FromException<int>(new InvalidOperationException());
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Las_llamadas_concurrentes_comparten_el_intento_en_curso()
    {
        var calls = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lazy = new ResettableAsyncLazy<int>(() =>
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        });

        var a = lazy.GetValueAsync();
        var b = lazy.GetValueAsync();
        var c = lazy.GetValueAsync();

        Assert.False(a.IsCompleted);
        gate.SetResult(5);

        Assert.Equal(new[] { 5, 5, 5 }, await Task.WhenAll(a, b, c));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Las_llamadas_concurrentes_ven_el_mismo_fallo_y_la_siguiente_reintenta()
    {
        var calls = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lazy = new ResettableAsyncLazy<int>(() =>
            Interlocked.Increment(ref calls) == 1 ? gate.Task : Task.FromResult(9));

        var a = lazy.GetValueAsync();
        var b = lazy.GetValueAsync();
        gate.SetException(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => a);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b);
        Assert.Equal(1, calls);

        Assert.Equal(9, await lazy.GetValueAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Una_fabrica_nula_lanza_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ResettableAsyncLazy<int>(null!));
    }
}
