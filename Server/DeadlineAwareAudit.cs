using System.Diagnostics;
using Drawmaha;

namespace Drawmaha.Server;

public sealed class DeadlineAwareAudit(IHandAudit sink, Action<TimeSpan> compensate) : IHandAudit
{
    public void Record(HandAuditEvent entry)
    {
        long started = Stopwatch.GetTimestamp();
        try { sink.Record(entry); }
        finally { compensate(Stopwatch.GetElapsedTime(started)); }
    }
}
