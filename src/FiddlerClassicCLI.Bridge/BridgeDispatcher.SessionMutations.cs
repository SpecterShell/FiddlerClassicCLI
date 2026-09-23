// Delegates session removal, SAZ archives, replay, and request composition to Fiddler on its UI thread.
using Fiddler;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeDispatcher
{
    /// <summary>
    /// Removes all sessions after verifying the caller supplied explicit confirmation.
    /// </summary>
    /// <param name="request">The destructive-operation confirmation.</param>
    private static ClearSessionsResponse ClearSessions(ClearSessionsRequest request)
    {
        if (!request.Confirm)
        {
            throw new BridgeOperationException(
                ErrorCodes.ConfirmationRequired,
                "Clearing sessions requires explicit confirmation.");
        }

        return FiddlerThread.Invoke(() =>
        {
            var before = new HashSet<int>(FiddlerApplication.UI.GetAllSessions().Select(session => session.id));
            FiddlerApplication.UI.actRemoveAllSessions();
            var remaining = new HashSet<int>(FiddlerApplication.UI.GetAllSessions().Select(session => session.id));
            before.ExceptWith(remaining);
            return new ClearSessionsResponse { RemovedCount = before.Count };
        });
    }

    /// <summary>
    /// Removes an exact, bounded set of sessions after explicit confirmation.
    /// </summary>
    /// <param name="request">The session IDs and destructive-operation confirmation.</param>
    private static RemoveSessionsResponse RemoveSessions(RemoveSessionsRequest request)
    {
        if (!request.Confirm)
        {
            throw new BridgeOperationException(
                ErrorCodes.ConfirmationRequired,
                "Removing sessions requires explicit confirmation.");
        }

        var ids = (request.SessionIds ?? Array.Empty<int>()).Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0 || ids.Length > ProtocolConstants.MaxSessionLimit || ids.Any(id => id <= 0))
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"Provide between 1 and {ProtocolConstants.MaxSessionLimit} positive session IDs.");
        }

        return FiddlerThread.Invoke(() =>
        {
            var requested = new HashSet<int>(ids);
            var selected = FiddlerApplication.UI.GetAllSessions()
                .Where(session => requested.Contains(session.id))
                .ToArray();
            var missing = requested.Except(selected.Select(session => session.id)).OrderBy(id => id).ToArray();
            if (missing.Length > 0)
            {
                throw new BridgeOperationException(ErrorCodes.NotFound, $"Sessions not found: {string.Join(", ", missing)}");
            }

            FiddlerApplication.UI.actRemoveRange(selected);
            return new RemoveSessionsResponse
            {
                RemovedCount = selected.Length,
                RemovedSessionIds = ids
            };
        });
    }

    /// <summary>
    /// Selects sessions, enforces overwrite confirmation, and exports them as a SAZ archive.
    /// </summary>
    /// <param name="request">The archive path, optional session IDs, and overwrite controls.</param>
    private static ArchiveResponse SaveSessions(SaveSessionsRequest request)
    {
        var path = ValidateArchivePath(request.Path, mustExist: false);
        if (File.Exists(path))
        {
            if (!request.Overwrite)
            {
                throw new BridgeOperationException(ErrorCodes.Conflict, $"Archive already exists: {path}");
            }

            if (!request.ConfirmOverwrite)
            {
                throw new BridgeOperationException(
                    ErrorCodes.ConfirmationRequired,
                    "Overwriting an existing archive requires explicit confirmation.");
            }
        }

        return FiddlerThread.Invoke(() =>
        {
            var allSessions = FiddlerApplication.UI.GetAllSessions();
            Session[] selected;

            if (request.SessionIds == null || request.SessionIds.Length == 0)
            {
                selected = allSessions;
            }
            else
            {
                var requestedIds = new HashSet<int>(request.SessionIds);
                selected = allSessions.Where(session => requestedIds.Contains(session.id)).ToArray();
                var missing = requestedIds.Except(selected.Select(session => session.id)).OrderBy(id => id).ToArray();
                if (missing.Length > 0)
                {
                    throw new BridgeOperationException(
                        ErrorCodes.NotFound,
                        $"Sessions not found: {string.Join(", ", missing)}");
                }
            }

            if (selected.Length == 0)
            {
                throw new BridgeOperationException(ErrorCodes.InvalidRequest, "There are no sessions to save.");
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (!Utilities.WriteSessionArchive(path, selected, string.Empty, false))
            {
                throw new BridgeOperationException(ErrorCodes.Internal, "Fiddler could not save the SAZ archive.");
            }

            return new ArchiveResponse { Path = path, SessionCount = selected.Length };
        });
    }

    /// <summary>
    /// Imports sessions from a validated existing SAZ archive.
    /// </summary>
    /// <param name="request">The absolute archive path to import.</param>
    private static ArchiveResponse LoadSessions(LoadSessionsRequest request)
    {
        var path = ValidateArchivePath(request.Path, mustExist: true);
        return FiddlerThread.Invoke(() =>
        {
            var imported = Utilities.ReadSessionArchive(path, false) ?? Array.Empty<Session>();
            FiddlerApplication.UI.AddImportedSessions(imported);
            return new ArchiveResponse { Path = path, SessionCount = imported.Length };
        });
    }

    /// <summary>
    /// Queues one captured session for replay without waiting for the resulting transaction.
    /// </summary>
    /// <param name="request">The session ID and unconditional replay choice.</param>
    private static QueuedResponse ReplaySession(ReplaySessionRequest request)
    {
        return FiddlerThread.Invoke(() =>
        {
            var session = FindSession(request.SessionId);
            var baselineSessionId = GetLatestSessionId();
            FiddlerApplication.UI.actReissueSessions(new[] { session }, request.Unconditional);
            return new QueuedResponse
            {
                Accepted = true,
                Message = $"Session {session.id} was queued for replay.",
                BaselineSessionId = baselineSessionId,
                ExpectedMethod = session.RequestMethod,
                ExpectedUrl = session.fullUrl
            };
        });
    }

    /// <summary>
    /// Queues a validated composed request through Fiddler without waiting for completion.
    /// </summary>
    /// <param name="request">The composed HTTP request.</param>
    private static QueuedResponse SendRequest(SendRequestRequest request)
    {
        var rawRequest = BuildRawRequest(request);
        return FiddlerThread.Invoke(() =>
        {
            var baselineSessionId = GetLatestSessionId();
            FiddlerObject.utilIssueRequest(rawRequest);
            return new QueuedResponse
            {
                Accepted = true,
                Message = $"{request.Method.ToUpperInvariant()} {request.Url} was queued.",
                BaselineSessionId = baselineSessionId,
                ExpectedMethod = request.Method.ToUpperInvariant(),
                ExpectedUrl = request.Url
            };
        });
    }

    /// <summary>
    /// Converts raw-request validation failures into stable bridge operation errors.
    /// </summary>
    /// <param name="request">The composed request to validate and serialize.</param>
    private static string BuildRawRequest(SendRequestRequest request)
    {
        if (!RawRequestBuilder.TryBuild(request, out var rawRequest, out var errorMessage))
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, errorMessage);
        }

        return rawRequest;
    }

    private static int GetLatestSessionId()
    {
        var sessions = FiddlerApplication.UI.GetAllSessions();
        return sessions.Length == 0 ? 0 : sessions.Max(session => session.id);
    }

    /// <summary>
    /// Normalizes a SAZ path and converts validation output into a bridge operation error.
    /// </summary>
    /// <param name="path">The user-supplied absolute archive path.</param>
    /// <param name="mustExist">Whether the archive file must already exist.</param>
    private static string ValidateArchivePath(string path, bool mustExist)
    {
        if (!ArchivePaths.TryValidate(path, mustExist, out var fullPath, out var errorCode, out var errorMessage))
        {
            throw new BridgeOperationException(errorCode, errorMessage);
        }

        return fullPath;
    }
}
