namespace Contracts.Notification;

/// <summary>
/// Puerto de un destino de notificación (log estructurado, webhook, ...). El
/// <c>CompositeNotifier</c> hace fan-out sobre todas las implementaciones registradas por DI;
/// <see cref="Name"/> identifica a cada una en sus mensajes de error y en los criterios de
/// aceptación.
/// </summary>
public interface INotifier
{
    /// <summary>Nombre corto del destino, p. ej. <c>"log"</c> o <c>"webhook"</c>.</summary>
    string Name { get; }

    /// <summary>Envía la notificación. Una excepción indica que este destino falló.</summary>
    Task NotifyAsync(VerdictChangeNotification notification, CancellationToken ct = default);
}
