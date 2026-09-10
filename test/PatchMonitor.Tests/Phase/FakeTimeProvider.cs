namespace PatchMonitor.Tests.Phase;

/// <summary>
/// Subclase mínima de <see cref="TimeProvider"/> con un "ahora" fijo y mutable, para que los
/// tests de la fase 3 (que comparan <c>StartTime</c> contra el reloj) sean determinísticos.
/// A propósito no se agrega el paquete <c>Microsoft.Extensions.TimeProvider.Testing</c>: el
/// <c>.csproj</c> de tests no gana ninguna referencia.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    public FakeTimeProvider(DateTimeOffset now) => Now = now;

    /// <summary>El instante que devuelve <see cref="GetUtcNow"/>. Mutable entre asserts.</summary>
    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
