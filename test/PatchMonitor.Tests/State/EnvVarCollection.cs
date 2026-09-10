using Xunit;

namespace PatchMonitor.Tests.State;

/// <summary>
/// Colección compartida por los tests que leen o escriben las variables de entorno de
/// <see cref="Contracts.State.StateOptions"/>. xUnit no corre en paralelo los tests de una
/// misma colección, así que <c>StateOptionsTests</c> y <c>ContinueAsNewTests</c> no se pisan
/// el proceso.
/// </summary>
[CollectionDefinition(Name)]
public sealed class EnvVarCollection
{
    public const string Name = "state-env-vars";
}
