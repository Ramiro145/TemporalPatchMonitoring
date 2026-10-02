using Common.Temporal;
using Xunit;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// La parte pura de <see cref="TemporalMonitorRunReader"/> (spec 15, M-8). El listado real
/// contra Visibility no tiene cobertura unitaria: se verifica contra el stack real.
/// </summary>
public class MonitorRunReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 30, 15, TimeSpan.Zero);

    [Fact]
    public void BuildWindowQuery_acota_por_StartTime_desde_hace_24h_hasta_un_dia_adelante()
    {
        var query = TemporalMonitorRunReader.BuildWindowQuery(Now, TimeSpan.FromHours(24));

        Assert.Equal(
            "StartTime BETWEEN '2026-10-01T12:30:15Z' AND '2026-10-03T12:30:15Z'", query);
    }

    [Fact]
    public void BuildWindowQuery_es_simple_sin_condiciones_compuestas()
    {
        var query = TemporalMonitorRunReader.BuildWindowQuery(Now, TimeSpan.FromHours(24));

        // La standard visibility se cuelga o rechaza compuestas (Construction.md §4, #1).
        Assert.DoesNotContain(" OR ", query);
        Assert.DoesNotContain("WorkflowType", query);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(query, " AND "));
    }

    [Fact]
    public void BuildWindowQuery_normaliza_a_UTC_aunque_el_reloj_tenga_offset()
    {
        var local = new DateTimeOffset(2026, 10, 2, 9, 30, 15, TimeSpan.FromHours(-3));

        Assert.Equal(
            TemporalMonitorRunReader.BuildWindowQuery(Now, TimeSpan.FromHours(24)),
            TemporalMonitorRunReader.BuildWindowQuery(local, TimeSpan.FromHours(24)));
    }
}
