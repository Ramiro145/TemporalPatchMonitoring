namespace Contracts.Domain;

/// <summary>
/// Resultado de evaluar un gate de salto de fase. Tri-estado a propósito: el
/// descubrimiento (spec 03) inspecciona un tope de ejecuciones por corrida, así que
/// "no sé" (<see cref="Inconclusive"/>) tiene que poder distinguirse de "todavía no"
/// (<see cref="Blocked"/>). Un booleano colapsaría ambos en <c>false</c>.
/// </summary>
public enum GateOutcome
{
    /// <summary>Los datos disponibles no alcanzan para afirmar ni bloquear el salto.</summary>
    Inconclusive = 0,

    /// <summary>Hay al menos una ejecución que impide el salto de fase.</summary>
    Blocked = 1,

    /// <summary>No queda ninguna ejecución bloqueante y los datos son completos.</summary>
    Ready = 2,
}
