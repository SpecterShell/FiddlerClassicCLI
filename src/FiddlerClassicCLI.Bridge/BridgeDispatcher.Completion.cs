// Waits for completed session snapshots using bounded completion notifications and polling.
using Fiddler;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeDispatcher
{
    /// <summary>
    /// Waits for the first completed session after a cursor that satisfies the reusable list filters.
    /// </summary>
    /// <param name="request">The exclusive ID cursor, filters, and bounded timeout.</param>
    private WaitForSessionResponse WaitForSession(WaitForSessionRequest request)
    {
        if (request.AfterId < 0)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The after-session ID cannot be negative.");
        }

        if (request.TimeoutMilliseconds < 1 || request.TimeoutMilliseconds > ProtocolConstants.MaxWaitMilliseconds)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"Wait timeout must be between 1 and {ProtocolConstants.MaxWaitMilliseconds} milliseconds.");
        }

        request.Filters = request.Filters ?? new ListSessionsRequest();
        request.Filters.MinId = Math.Max(request.Filters.MinId ?? 0, request.AfterId + 1);
        request.Filters.NewestFirst = false;
        request.Filters.Limit = 1;
        ValidateFilters(request.Filters);

        var deadline = DateTime.UtcNow.AddMilliseconds(request.TimeoutMilliseconds);
        while (!_disposed)
        {
            long observedSequence;
            lock (_completionLock)
            {
                observedSequence = _completionSequence;
            }

            var inspection = FiddlerThread.Invoke(() =>
            {
                var snapshot = FiddlerApplication.UI.GetAllSessions();
                var latestId = snapshot.Length == 0 ? request.AfterId : snapshot.Max(session => session.id);
                var match = ApplyFilters(snapshot, request.Filters)
                    .Where(session => session.state == SessionStates.Done)
                    .OrderBy(session => session.id)
                    .FirstOrDefault();
                return new WaitForSessionResponse
                {
                    Matched = match != null,
                    Session = match == null ? null : ToSummary(match),
                    LatestSessionId = latestId
                };
            });
            if (inspection.Matched)
            {
                return inspection;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return inspection;
            }

            lock (_completionLock)
            {
                if (_completionSequence == observedSequence && !_disposed)
                {
                    var fallbackInterval = TimeSpan.FromMilliseconds(250);
                    Monitor.Wait(_completionLock, remaining < fallbackInterval ? remaining : fallbackInterval);
                }
            }
        }

        throw new BridgeOperationException(ErrorCodes.Unavailable, "The Fiddler bridge is shutting down.");
    }

    /// <summary>
    /// Records only completed session IDs in a bounded buffer and wakes matching wait operations.
    /// </summary>
    /// <param name="session">The session that reached Fiddler's completed state.</param>
    private void OnAfterSessionComplete(Session session)
    {
        lock (_completionLock)
        {
            _completedSessionIds.Enqueue(session.id);
            while (_completedSessionIds.Count > CompletedSessionBufferCapacity)
            {
                _completedSessionIds.Dequeue();
            }

            _completionSequence++;
            Monitor.PulseAll(_completionLock);
        }
    }
}
