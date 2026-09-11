using Contracts.Monitor;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;

namespace Common.Temporal
{
    /// <summary>
    /// Implementación de <see cref="IScheduleController"/> sobre
    /// <see cref="ITemporalClient.GetScheduleHandle(string)"/>, al lado del
    /// <see cref="ScheduleBootstrapper"/> que crea ese mismo Schedule. Captura
    /// <see cref="RpcException"/> con <see cref="RpcException.StatusCode.NotFound"/> como
    /// "no existe"; cualquier otro error de RPC propaga.
    /// </summary>
    public sealed class TemporalScheduleController : IScheduleController
    {
        private readonly Lazy<Task<ITemporalClient>> _client;
        private readonly MonitorOptions _options;

        public TemporalScheduleController(Lazy<Task<ITemporalClient>> client, MonitorOptions options)
        {
            _client = client;
            _options = options;
        }

        public async Task<ScheduleStatus?> DescribeAsync(CancellationToken ct = default)
        {
            var client = await _client.Value.ConfigureAwait(false);
            var handle = client.GetScheduleHandle(_options.ScheduleId);

            try
            {
                var description = await handle.DescribeAsync().ConfigureAwait(false);
                return ToStatus(description);
            }
            catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<bool> PauseAsync(string? note, CancellationToken ct = default)
        {
            var client = await _client.Value.ConfigureAwait(false);
            var handle = client.GetScheduleHandle(_options.ScheduleId);

            try
            {
                await handle.PauseAsync(note).ConfigureAwait(false);
                return true;
            }
            catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
            {
                return false;
            }
        }

        public async Task<bool> UnpauseAsync(string? note, CancellationToken ct = default)
        {
            var client = await _client.Value.ConfigureAwait(false);
            var handle = client.GetScheduleHandle(_options.ScheduleId);

            try
            {
                await handle.UnpauseAsync(note).ConfigureAwait(false);
                return true;
            }
            catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
            {
                return false;
            }
        }

        public async Task<bool> TriggerAsync(CancellationToken ct = default)
        {
            var client = await _client.Value.ConfigureAwait(false);
            var handle = client.GetScheduleHandle(_options.ScheduleId);

            try
            {
                // Sin fijar Overlap: hereda la política del Schedule (Overlap = Skip, spec 06).
                await handle.TriggerAsync().ConfigureAwait(false);
                return true;
            }
            catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
            {
                return false;
            }
        }

        private ScheduleStatus ToStatus(ScheduleDescription description)
        {
            var info = description.Info;
            var interval = description.Schedule.Spec.Intervals?.FirstOrDefault()?.Every ?? TimeSpan.Zero;
            var lastRunAt = info.RecentActions.LastOrDefault()?.StartedAt;
            var nextRunAt = info.NextActionTimes.FirstOrDefault();

            return new ScheduleStatus(
                description.Id,
                description.Schedule.State?.Paused ?? false,
                description.Schedule.State?.Note,
                interval,
                lastRunAt.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(lastRunAt.Value, DateTimeKind.Utc)) : null,
                nextRunAt == default ? null : new DateTimeOffset(DateTime.SpecifyKind(nextRunAt, DateTimeKind.Utc)),
                info.RunningActions.Count,
                info.NumActions);
        }
    }
}
