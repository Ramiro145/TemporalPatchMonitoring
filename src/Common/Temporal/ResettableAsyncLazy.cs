using System;
using System.Threading.Tasks;

namespace Common.Temporal
{
    /// <summary>
    /// Lazy asíncrono que cachea los éxitos pero no los fallos (spec 15, M-5). A diferencia de
    /// <c>Lazy&lt;Task&lt;T&gt;&gt;</c>, que guarda la tarea fallida para siempre, acá una fábrica que
    /// falló (o se canceló) se vuelve a ejecutar en la llamada siguiente: el proceso reconecta
    /// al cluster sin reiniciarse. Las llamadas concurrentes comparten el intento en curso.
    /// </summary>
    public sealed class ResettableAsyncLazy<T>
    {
        private readonly Func<Task<T>> _factory;
        private readonly object _gate = new();
        private Task<T>? _task;

        public ResettableAsyncLazy(Func<Task<T>> factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>
        /// Devuelve el valor cacheado, el intento en curso, o inicia uno nuevo si no hay nada o
        /// el anterior terminó en fallo. Quien reciba una tarea fallida ve su excepción; el
        /// reintento ocurre en la llamada siguiente.
        /// </summary>
        public Task<T> GetValueAsync()
        {
            lock (_gate)
            {
                if (_task is null || _task.IsFaulted || _task.IsCanceled)
                {
                    _task = StartFactory();
                }

                return _task;
            }
        }

        // Una fábrica que lanza de forma síncrona se convierte en tarea fallida, igual que si
        // lanzara dentro de un async: así el reset de arriba la trata de la misma manera.
        private Task<T> StartFactory()
        {
            try
            {
                return _factory();
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }
    }
}
