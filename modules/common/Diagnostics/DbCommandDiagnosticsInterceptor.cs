using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Minicloud.Common.Diagnostics;

public sealed class DbCommandDiagnosticsInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<Guid, Activity> activities = new();

    private void Start(CommandEventData data)
    {
        var parent = Activity.Current;
        var activity = OperatorDiagnostics.Activities.StartActivity("db.command", ActivityKind.Client);
        if (activity is null) return;
        activity.SetTag("operation", "ef.command");
        activities[data.CommandId] = activity;
        Activity.Current = parent;
    }

    private void Finish(CommandEndEventData data, string outcome)
    {
        OperatorDiagnostics.RecordCommand("ef.command", outcome, data.Duration);
        if (activities.TryRemove(data.CommandId, out var activity))
        {
            activity.SetTag("outcome", outcome);
            if (outcome == "error") activity.SetStatus(ActivityStatusCode.Error);
            activity.Stop();
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) { Start(eventData); return result; }
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result) { Start(eventData); return result; }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) { Start(eventData); return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Start(eventData); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) { Start(eventData); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Start(eventData); return ValueTask.FromResult(result); }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) { Finish(eventData, "success"); return result; }
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result) { Finish(eventData, "success"); return result; }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result) { Finish(eventData, "success"); return result; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default) { Finish(eventData, "success"); return ValueTask.FromResult(result); }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default) { Finish(eventData, "success"); return ValueTask.FromResult(result); }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default) { Finish(eventData, "success"); return ValueTask.FromResult(result); }
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Finish(eventData, "error");
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default) { Finish(eventData, "error"); return Task.CompletedTask; }
    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) => Finish(eventData, "cancelled");
    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default) { Finish(eventData, "cancelled"); return Task.CompletedTask; }
}
