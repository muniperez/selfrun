using System.Diagnostics.Metrics;
using Minicloud.Common.Diagnostics;

namespace Minicloud.WorkspaceGateway;

// Fixed, low-cardinality gateway signals; never tag hosts, sessions, paths or targets.
internal static class GatewayDiagnostics
{
    public const string ActiveConnectionsName = "minicloud.gateway.connections.active";
    public const string AdmissionWaitName = "minicloud.gateway.admission.wait";
    public const string RelayBytesName = "minicloud.gateway.relay.bytes";
    public const string WebSocketClosesName = "minicloud.gateway.websocket.closes";

    private static readonly UpDownCounter<long> ActiveConnections =
        OperatorDiagnostics.Meter.CreateUpDownCounter<long>(ActiveConnectionsName, "{connection}");
    private static readonly Histogram<double> AdmissionWait =
        OperatorDiagnostics.Meter.CreateHistogram<double>(AdmissionWaitName, "s");
    private static readonly Counter<long> RelayBytes =
        OperatorDiagnostics.Meter.CreateCounter<long>(RelayBytesName, "By");
    private static readonly Counter<long> WebSocketCloses =
        OperatorDiagnostics.Meter.CreateCounter<long>(WebSocketClosesName, "{connection}");

    public static void Admission(string outcome, TimeSpan wait) =>
        AdmissionWait.Record(wait.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));

    public static void Active(string operation, int delta) =>
        ActiveConnections.Add(delta, new KeyValuePair<string, object?>("operation", operation));

    public static void Relayed(string operation, string direction, int bytes)
    {
        if (bytes > 0 && RelayBytes.Enabled)
            RelayBytes.Add(bytes, new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("direction", direction));
    }

    public static void WebSocketClosed(string outcome) =>
        WebSocketCloses.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
